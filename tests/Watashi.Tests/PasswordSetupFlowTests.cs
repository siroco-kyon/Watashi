using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Auth;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

/// <summary>初回パスワード設定 (Windows 統合認証で本人確認 → 本人がパスワードを決める) のフロー。</summary>
public class PasswordSetupFlowTests
{
    private const string WindowsAccount = @"CORP\G012345";
    private const string Gid = "G012345";
    private const string GoodPassword = "NewStrongPassword2026!";

    private static WindowsAuthOptions Options() => new()
    {
        Mode = WindowsAuthModes.Negotiate,
        DomainMatch = WindowsAuthDomainMatchModes.IgnoreDomain,
    };

    private static AuthService Build(TestDb db)
        => new(db.Db, new AuthServiceOptions
        {
            Secret = "TEST-SECRET-At-Least-32-Bytes-Long-XXXXXXXXXXXXXXX",
            Issuer = "Watashi", Audience = "Watashi",
            AccessTokenMinutes = 15, RefreshTokenDays = 30,
        });

    private static async Task<User> SeedAsync(
        TestDb db, bool pending = true, bool locked = false, DateTime? setupExpiresAt = null)
    {
        var now = DateTime.UtcNow;
        var u = new User
        {
            Username = Gid,
            PasswordHash = pending ? PasswordSetup.CreateUnusableHash() : BCrypt.Net.BCrypt.HashPassword("Existing1!@#"),
            IsPasswordSetupPending = pending,
            IsLocked = locked,
            PasswordSetupExpiresAt = setupExpiresAt,
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(30),
            CreatedAt = now,
        };
        db.Db.Users.Add(u);
        db.Db.SystemSettings.Add(new SystemSetting { Key = SettingKeys.PasswordExpiryDays, Value = "90", UpdatedAt = now });
        await db.Db.SaveChangesAsync();
        return u;
    }

    // ===== prepare =====

    [Fact]
    public async Task Prepare_is_eligible_for_the_matching_pending_user()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        var (eligible, _) = await Build(db).PreparePasswordSetupAsync(Gid, WindowsAccount, Options(), null, null);

        eligible.Should().BeTrue();
    }

    [Fact]
    public async Task Prepare_returns_the_setup_expiry()
    {
        using var db = new TestDb();
        var due = DateTime.UtcNow.AddDays(7);
        await SeedAsync(db, setupExpiresAt: due);

        var (eligible, expiresAt) = await Build(db).PreparePasswordSetupAsync(Gid, WindowsAccount, Options(), null, null);

        eligible.Should().BeTrue();
        expiresAt.Should().BeCloseTo(due, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Prepare_rejects_a_different_windows_account()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        var (eligible, _) = await Build(db).PreparePasswordSetupAsync(Gid, @"CORP\G999999", Options(), null, null);

        eligible.Should().BeFalse();
        var log = await db.Db.AuditLogs.AsNoTracking()
            .SingleAsync(l => l.Operation == AuthOperations.PasswordSetupIdentityMismatch);
        log.Result.Should().Be(AuditResults.Warning);
        log.Username.Should().Be(Gid);
    }

    [Fact]
    public async Task Prepare_rejects_an_unknown_username_without_leaking()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        // 自分の Windows アカウントで、存在しない ID を問い合わせる。
        var (eligible, _) = await Build(db).PreparePasswordSetupAsync("G999999", @"CORP\G999999", Options(), null, null);

        eligible.Should().BeFalse();
    }

    [Fact]
    public async Task Prepare_rejects_a_user_whose_password_is_already_set()
    {
        using var db = new TestDb();
        await SeedAsync(db, pending: false);

        var (eligible, _) = await Build(db).PreparePasswordSetupAsync(Gid, WindowsAccount, Options(), null, null);

        eligible.Should().BeFalse();
    }

    [Fact]
    public async Task Prepare_rejects_a_locked_user()
    {
        using var db = new TestDb();
        await SeedAsync(db, locked: true);

        var (eligible, _) = await Build(db).PreparePasswordSetupAsync(Gid, WindowsAccount, Options(), null, null);

        eligible.Should().BeFalse();
    }

    [Fact]
    public async Task Prepare_rejects_an_expired_setup_window()
    {
        using var db = new TestDb();
        await SeedAsync(db, setupExpiresAt: DateTime.UtcNow.AddDays(-1));

        var (eligible, _) = await Build(db).PreparePasswordSetupAsync(Gid, WindowsAccount, Options(), null, null);

        eligible.Should().BeFalse();
    }

    [Fact]
    public async Task Prepare_records_a_successful_identity_check()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        await Build(db).PreparePasswordSetupAsync(Gid, WindowsAccount, Options(), "10.0.0.5", "PC-01");

        var log = await db.Db.AuditLogs.AsNoTracking()
            .SingleAsync(l => l.Operation == AuthOperations.PasswordSetupRequested);
        log.Result.Should().Be(AuditResults.Success);
        log.ClientIp.Should().Be("10.0.0.5");
        log.ClientHostname.Should().Be("PC-01");
    }

    // ===== initialize =====

    [Fact]
    public async Task Initialize_sets_the_password_and_issues_tokens()
    {
        using var db = new TestDb();
        var u = await SeedAsync(db);
        var svc = Build(db);

        var (response, error) = await svc.CompletePasswordSetupAsync(
            Gid, GoodPassword, WindowsAccount, Options(), null, "PC-01");

        error.Should().BeNull();
        response!.AccessToken.Should().NotBeEmpty();
        response.RefreshToken.Should().NotBeEmpty();
        // 本人が決めたパスワードなので、直後にもう一度変更させる必要はない。
        response.MustChangePassword.Should().BeFalse();

        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.AsNoTracking().FirstAsync(x => x.Id == u.Id);
        fresh.IsPasswordSetupPending.Should().BeFalse();
        fresh.PasswordSetupExpiresAt.Should().BeNull();
        fresh.MustChangePassword.Should().BeFalse();
        fresh.WindowsAccountName.Should().Be(WindowsAccount);
        fresh.PasswordExpiresAt.Should().BeAfter(DateTime.UtcNow.AddDays(80));
    }

    [Fact]
    public async Task Initialize_makes_normal_login_work_afterwards()
    {
        using var db = new TestDb();
        await SeedAsync(db);
        var svc = Build(db);

        await svc.CompletePasswordSetupAsync(Gid, GoodPassword, WindowsAccount, Options(), null, null);

        var login = await svc.LoginAsync(Gid, GoodPassword, clientIp: null);
        login.Failure.Should().BeNull();
        login.Response.Should().NotBeNull();
    }

    [Fact]
    public async Task Initialize_rejects_a_different_windows_account()
    {
        using var db = new TestDb();
        var u = await SeedAsync(db);

        var (response, error) = await Build(db).CompletePasswordSetupAsync(
            Gid, GoodPassword, @"CORP\G999999", Options(), null, null);

        response.Should().BeNull();
        error.Should().NotBeNullOrEmpty();

        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.AsNoTracking().FirstAsync(x => x.Id == u.Id);
        fresh.IsPasswordSetupPending.Should().BeTrue();
        (await db.Db.AuditLogs.CountAsync(l => l.Operation == AuthOperations.PasswordSetupIdentityMismatch))
            .Should().Be(1);
    }

    [Fact]
    public async Task Initialize_rejects_a_password_that_violates_the_policy()
    {
        using var db = new TestDb();
        var u = await SeedAsync(db);

        var (response, error) = await Build(db).CompletePasswordSetupAsync(
            Gid, "short", WindowsAccount, Options(), null, null);

        response.Should().BeNull();
        // ポリシー違反だけは具体的に返す (本人が直せる指摘なので)。
        error.Should().Contain("12");

        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.AsNoTracking().FirstAsync(x => x.Id == u.Id);
        fresh.IsPasswordSetupPending.Should().BeTrue();
    }

    [Fact]
    public async Task Initialize_rejects_an_already_completed_user()
    {
        using var db = new TestDb();
        await SeedAsync(db, pending: false);

        var (response, error) = await Build(db).CompletePasswordSetupAsync(
            Gid, GoodPassword, WindowsAccount, Options(), null, null);

        response.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
        // 既存パスワードが上書きされていないこと。
        var login = await Build(db).LoginAsync(Gid, "Existing1!@#", clientIp: null);
        login.Response.Should().NotBeNull();
    }

    [Fact]
    public async Task Initialize_rejects_an_expired_setup_window()
    {
        using var db = new TestDb();
        await SeedAsync(db, setupExpiresAt: DateTime.UtcNow.AddDays(-1));

        var (response, _) = await Build(db).CompletePasswordSetupAsync(
            Gid, GoodPassword, WindowsAccount, Options(), null, null);

        response.Should().BeNull();
    }

    [Fact]
    public async Task Initialize_cannot_be_applied_twice()
    {
        // 二重送信の後半は、更新条件 (IsPasswordSetupPending) に阻まれて 0 件更新になる。
        // その状況を作るため、エンティティを読み込んだ後に DB 側だけ確定させる。
        using var db = new TestDb();
        var u = await SeedAsync(db);
        var svc = Build(db);

        // 先に「読み込み済みだが DB は既に確定済み」の状態を作る。
        db.Db.Users.Attach(u);
        await db.Db.Database.ExecuteSqlRawAsync(
            "UPDATE Users SET IsPasswordSetupPending = 0, PasswordHash = 'ALREADY-SET' WHERE Id = {0}", u.Id);

        var (response, error) = await svc.CompletePasswordSetupAsync(
            Gid, GoodPassword, WindowsAccount, Options(), null, null);

        response.Should().BeNull();
        error.Should().NotBeNullOrEmpty();

        // 先に確定した側のハッシュが上書きされていないこと。
        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.AsNoTracking().FirstAsync(x => x.Id == u.Id);
        fresh.PasswordHash.Should().Be("ALREADY-SET");
        (await db.Db.AuditLogs.CountAsync(l => l.Operation == AuthOperations.PasswordSetupRejected))
            .Should().Be(1);
    }

    [Fact]
    public async Task Initialize_records_a_success_audit_entry()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        await Build(db).CompletePasswordSetupAsync(Gid, GoodPassword, WindowsAccount, Options(), "10.0.0.7", "PC-01");

        var log = await db.Db.AuditLogs.AsNoTracking()
            .SingleAsync(l => l.Operation == AuthOperations.PasswordSetupSucceeded);
        log.Result.Should().Be(AuditResults.Success);
        log.Username.Should().Be(Gid);
        log.ClientIp.Should().Be("10.0.0.7");
    }

    [Fact]
    public async Task Initialize_does_not_log_an_identity_mismatch_for_the_rightful_owner()
    {
        // 正規化した Windows 名と Watashi ユーザー名は一致しているので、
        // 通常ログインの「別人ログイン」警告が誤って出てはいけない。
        using var db = new TestDb();
        await SeedAsync(db);

        await Build(db).CompletePasswordSetupAsync(Gid, GoodPassword, WindowsAccount, Options(), null, "PC-01");

        (await db.Db.AuditLogs.CountAsync(l => l.Operation == AuthOperations.LoginIdentityMismatch))
            .Should().Be(0);
    }

    // ===== 期限計算 =====

    [Fact]
    public async Task Setup_expiry_defaults_to_unlimited()
    {
        using var db = new TestDb();
        await SeedAsync(db);

        (await Build(db).ComputeSetupExpiryAsync(DateTime.UtcNow)).Should().BeNull();
    }

    [Fact]
    public async Task Setup_expiry_honours_the_configured_days()
    {
        using var db = new TestDb();
        await SeedAsync(db);
        var from = DateTime.UtcNow;
        db.Db.SystemSettings.Add(new SystemSetting
        {
            Key = SettingKeys.PasswordSetupExpiryDays, Value = "7", UpdatedAt = from,
        });
        await db.Db.SaveChangesAsync();

        var due = await Build(db).ComputeSetupExpiryAsync(from);

        due.Should().BeCloseTo(from.AddDays(7), TimeSpan.FromSeconds(1));
    }
}
