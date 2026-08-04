using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

/// <summary>
/// 初回パスワード設定待ち (IsPasswordSetupPending) のアカウントが、
/// どのログイン経路からも通らないことを固定するテスト。
/// </summary>
public class PasswordSetupPendingTests
{
    private const string KnownPassword = "Admin123!@#";

    private static AuthService Build(TestDb db)
        => new(db.Db, new AuthServiceOptions
        {
            Secret = "TEST-SECRET-At-Least-32-Bytes-Long-XXXXXXXXXXXXXXX",
            Issuer = "Watashi", Audience = "Watashi",
            AccessTokenMinutes = 15, RefreshTokenDays = 30,
        });

    /// <summary>初回設定待ちユーザーを作る。実運用と同じく使用不能ハッシュを入れる。</summary>
    private static async Task<User> SeedPendingUserAsync(TestDb db, string name = "alice", bool admin = false)
        => await SeedAsync(db, name, PasswordSetup.CreateUnusableHash(), pending: true, admin);

    private static async Task<User> SeedAsync(TestDb db, string name, string hash, bool pending, bool admin = false)
    {
        var now = DateTime.UtcNow;
        var u = new User
        {
            Username = name,
            PasswordHash = hash,
            IsAdmin = admin,
            IsPasswordSetupPending = pending,
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(30),
            CreatedAt = now,
        };
        db.Db.Users.Add(u);
        await db.Db.SaveChangesAsync();
        return u;
    }

    // ===== 通常ログイン =====

    [Fact]
    public async Task Login_for_pending_user_fails_as_invalid_credentials()
    {
        using var db = new TestDb();
        await SeedPendingUserAsync(db);
        var svc = Build(db);

        var result = await svc.LoginAsync("alice", KnownPassword, clientIp: null);

        result.Response.Should().BeNull();
        result.Failure.Should().Be(LoginFailureReason.InvalidCredentials);
    }

    [Fact]
    public async Task Login_for_pending_user_does_not_increment_failure_counter()
    {
        // 本人が設定前に何度かログインを試みても、アカウントがロックされてはならない。
        // = bcrypt 照合の手前で分岐していることの証明。
        using var db = new TestDb();
        var u = await SeedPendingUserAsync(db);
        var svc = Build(db);

        for (var i = 0; i < 20; i++)
            await svc.LoginAsync("alice", KnownPassword, clientIp: null);

        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.AsNoTracking().FirstAsync(x => x.Id == u.Id);
        fresh.FailedLoginCount.Should().Be(0);
        fresh.IsLocked.Should().BeFalse();
    }

    [Fact]
    public async Task Login_for_pending_user_is_indistinguishable_from_unknown_user()
    {
        // 未設定 ID の存在を推測されないよう、応答を「存在しないユーザー」と一致させる。
        using var db = new TestDb();
        await SeedPendingUserAsync(db);
        var svc = Build(db);

        var pending = await svc.LoginAsync("alice", KnownPassword, clientIp: null);
        var unknown = await svc.LoginAsync("nobody", KnownPassword, clientIp: null);

        pending.Failure.Should().Be(unknown.Failure);
        pending.Response.Should().BeNull();
        unknown.Response.Should().BeNull();
    }

    [Fact]
    public async Task Login_for_pending_user_is_rejected_even_when_the_hash_matches()
    {
        // フラグが権威であることの確認。ハッシュが既知のパスワードと一致していても通さない。
        // (使用不能ハッシュの生成漏れがあっても多重防御が効くことを固定する)
        using var db = new TestDb();
        await SeedAsync(db, "alice", BCrypt.Net.BCrypt.HashPassword(KnownPassword), pending: true);
        var svc = Build(db);

        var result = await svc.LoginAsync("alice", KnownPassword, clientIp: null);

        result.Response.Should().BeNull();
        result.Failure.Should().Be(LoginFailureReason.InvalidCredentials);
    }

    [Fact]
    public async Task Login_succeeds_once_the_pending_flag_is_cleared()
    {
        using var db = new TestDb();
        var u = await SeedAsync(db, "alice", BCrypt.Net.BCrypt.HashPassword(KnownPassword), pending: true);
        var svc = Build(db);

        (await svc.LoginAsync("alice", KnownPassword, clientIp: null)).Response.Should().BeNull();

        u.IsPasswordSetupPending = false;
        await db.Db.SaveChangesAsync();

        var result = await svc.LoginAsync("alice", KnownPassword, clientIp: null);
        result.Failure.Should().BeNull();
        result.Response!.AccessToken.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Login_for_pending_user_is_recorded_in_the_audit_log()
    {
        using var db = new TestDb();
        var u = await SeedPendingUserAsync(db);
        var svc = Build(db);

        await svc.LoginAsync("alice", KnownPassword, clientIp: "10.0.0.9");

        var log = await db.Db.AuditLogs.AsNoTracking()
            .SingleAsync(l => l.Operation == AuthOperations.LoginFailed);
        log.UserId.Should().Be(u.Id);
        log.Username.Should().Be("alice");
        log.ErrorMessage.Should().Be("password_setup_pending");
        // AuditLog.Result には CHECK 制約 (success/failure/warning) がある。
        log.Result.Should().Be(AuditResults.Failure);
    }

    // ===== 自動ログイン =====

    [Fact]
    public async Task AutoLogin_for_pending_user_is_rejected()
    {
        // 記憶済み端末が残っていても、未設定ユーザーをパスワード無しで通してはならない。
        using var db = new TestDb();
        var u = await SeedAsync(db, "alice", BCrypt.Net.BCrypt.HashPassword(KnownPassword), pending: false);
        var svc = Build(db);
        var trust = await svc.TrustDeviceAsync(u.Id, "PC-01", "alice");

        // 端末登録後にユーザーを未設定へ戻す。
        u.IsPasswordSetupPending = true;
        await db.Db.SaveChangesAsync();

        var result = await svc.AutoLoginAsync("PC-01", "alice", trust.DeviceToken, clientIp: null);

        result.Response.Should().BeNull();
        result.Failure.Should().Be(LoginFailureReason.InvalidCredentials);
    }

    [Fact]
    public async Task AutoLogin_for_pending_user_is_recorded_in_the_audit_log()
    {
        // 不変条件 (未設定化時に端末も失効させる) が壊れたことを検知できるようにする。
        using var db = new TestDb();
        var u = await SeedAsync(db, "alice", BCrypt.Net.BCrypt.HashPassword(KnownPassword), pending: false);
        var svc = Build(db);
        var trust = await svc.TrustDeviceAsync(u.Id, "PC-01", "alice");
        u.IsPasswordSetupPending = true;
        await db.Db.SaveChangesAsync();

        await svc.AutoLoginAsync("PC-01", "alice", trust.DeviceToken, clientIp: null);

        var log = await db.Db.AuditLogs.AsNoTracking()
            .SingleAsync(l => l.Operation == AuthOperations.LoginFailed);
        log.ErrorMessage.Should().Be("password_setup_pending");
    }

    // ===== 使用不能ハッシュ =====

    [Fact]
    public void Unusable_hash_never_verifies()
    {
        var hash = PasswordSetup.CreateUnusableHash();

        BCrypt.Net.BCrypt.Verify("", hash).Should().BeFalse();
        BCrypt.Net.BCrypt.Verify(" ", hash).Should().BeFalse();
        BCrypt.Net.BCrypt.Verify(KnownPassword, hash).Should().BeFalse();
        BCrypt.Net.BCrypt.Verify("password", hash).Should().BeFalse();
    }

    [Fact]
    public void Unusable_hash_is_unique_per_call()
    {
        PasswordSetup.CreateUnusableHash().Should().NotBe(PasswordSetup.CreateUnusableHash());
    }

    // ===== 既存ユーザーへの非影響 =====

    [Fact]
    public async Task Existing_users_default_to_not_pending()
    {
        // migration は IsPasswordSetupPending を DEFAULT 0 で追加する。
        // 既存ユーザー相当 (フラグを明示しない) がそのままログインできることを固定する。
        using var db = new TestDb();
        var now = DateTime.UtcNow;
        db.Db.Users.Add(new User
        {
            Username = "legacy",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(KnownPassword),
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(30),
            CreatedAt = now,
        });
        await db.Db.SaveChangesAsync();

        var result = await Build(db).LoginAsync("legacy", KnownPassword, clientIp: null);

        result.Failure.Should().BeNull();
        result.Response.Should().NotBeNull();
    }
}
