using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

/// <summary>
/// AuthServiceTests を補完する追加テスト群:
/// アイドルタイムアウト伝播 / トークン再利用検知 / 期限切れ refresh / TrustDevice の同期化など。
/// </summary>
public class AuthServiceExtraTests
{
    private static AuthService Build(TestDb db) => new(db.Db, new AuthServiceOptions
    {
        Secret = "TEST-SECRET-At-Least-32-Bytes-Long-XXXXXXXXXXXXXXX",
        Issuer = "Watashi", Audience = "Watashi",
        AccessTokenMinutes = 15, RefreshTokenDays = 30,
    });

    private static async Task<User> SeedUserAsync(
        TestDb db,
        int idleMinutes = 30,
        int passwordExpiryDays = 90,
        int passwordWarningDays = 14)
    {
        var u = new User
        {
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!@#"),
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(passwordExpiryDays),
            CreatedAt = DateTime.UtcNow,
        };
        db.Db.Users.Add(u);
        db.Db.SystemSettings.Add(new SystemSetting { Key = SettingKeys.SessionIdleMinutes, Value = idleMinutes.ToString(), UpdatedAt = DateTime.UtcNow });
        db.Db.SystemSettings.Add(new SystemSetting { Key = SettingKeys.PasswordExpiryDays, Value = passwordExpiryDays.ToString(), UpdatedAt = DateTime.UtcNow });
        db.Db.SystemSettings.Add(new SystemSetting { Key = SettingKeys.PasswordWarningDays, Value = passwordWarningDays.ToString(), UpdatedAt = DateTime.UtcNow });
        await db.Db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task Login_should_propagate_session_idle_minutes_setting()
    {
        using var db = new TestDb();
        await SeedUserAsync(db, idleMinutes: 15);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        login.Response!.IdleMinutes.Should().Be(15);
    }

    [Fact]
    public async Task Login_should_fallback_to_default_idle_when_setting_invalid()
    {
        using var db = new TestDb();
        var u = new User
        {
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!@#"),
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
        };
        db.Db.Users.Add(u);
        db.Db.SystemSettings.Add(new SystemSetting { Key = SettingKeys.SessionIdleMinutes, Value = "not-a-number", UpdatedAt = DateTime.UtcNow });
        await db.Db.SaveChangesAsync();

        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        login.Response!.IdleMinutes.Should().Be(30);
    }

    [Fact]
    public async Task Login_should_propagate_password_warning_days_setting()
    {
        using var db = new TestDb();
        await SeedUserAsync(db, passwordWarningDays: 7);
        var svc = Build(db);

        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);

        login.Response!.PasswordWarningDays.Should().Be(7);
    }

    [Fact]
    public async Task Login_should_fallback_to_default_password_warning_days_when_setting_missing()
    {
        using var db = new TestDb();
        var user = new User
        {
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!@#"),
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
        };
        db.Db.Users.Add(user);
        await db.Db.SaveChangesAsync();
        var svc = Build(db);

        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);

        login.Response!.PasswordWarningDays.Should().Be(14);
    }

    [Fact]
    public async Task Refresh_should_rotate_refresh_token_id_each_call()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        var first = login.Response!;

        var (res1, _) = await svc.RefreshAsync(first.RefreshTokenId, first.RefreshToken);
        res1.Should().NotBeNull();
        res1!.RefreshTokenId.Should().NotBe(first.RefreshTokenId);
        res1.RefreshToken.Should().NotBe(first.RefreshToken);

        // 古い token は失効済み (再利用検知)。
        var (replay, err) = await svc.RefreshAsync(first.RefreshTokenId, first.RefreshToken);
        replay.Should().BeNull();
        err.Should().Be("token_reuse_detected");
    }

    [Fact]
    public async Task Refresh_token_reuse_detection_revokes_entire_family()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        var first = login.Response!;
        var (res1, _) = await svc.RefreshAsync(first.RefreshTokenId, first.RefreshToken);
        var (res2, _) = await svc.RefreshAsync(res1!.RefreshTokenId, res1.RefreshToken);
        res2.Should().NotBeNull();

        // 失効済みの旧トークン (first) を再提示するとファミリー全失効が起きる:
        // 直近の有効トークン (res2) も失効する。
        await svc.RefreshAsync(first.RefreshTokenId, first.RefreshToken);

        db.Db.ChangeTracker.Clear();
        var live = await db.Db.RefreshTokens.AsNoTracking().Where(t => t.UserId == u.Id && !t.IsRevoked).ToListAsync();
        live.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_with_expired_token_fails()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        // ExpiresAt を過去に書き換えて期限切れを再現。
        var tok = await db.Db.RefreshTokens.FirstAsync(t => t.Id == login.Response!.RefreshTokenId);
        tok.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await db.Db.SaveChangesAsync();
        db.Db.ChangeTracker.Clear();

        var (res, err) = await svc.RefreshAsync(login.Response!.RefreshTokenId, login.Response.RefreshToken);
        res.Should().BeNull();
        err.Should().Be("invalid_token");
    }

    [Fact]
    public async Task Refresh_with_unknown_token_id_fails()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var (res, err) = await svc.RefreshAsync("does-not-exist", "whatever");
        res.Should().BeNull();
        err.Should().Be("invalid_token");
    }

    [Fact]
    public async Task Refresh_with_wrong_plain_value_fails()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        var (res, err) = await svc.RefreshAsync(login.Response!.RefreshTokenId, "BOGUS-PLAIN");
        res.Should().BeNull();
        err.Should().Be("invalid_token");
    }

    [Fact]
    public async Task ChangePassword_with_wrong_current_returns_error()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var (res, err) = await svc.ChangePasswordAsync(u.Id, "WRONG", "NewStrongPassword2026!");
        res.Should().BeNull();
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TrustDevice_assigns_unique_token_per_invocation()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var t1 = await svc.TrustDeviceAsync(u.Id, "PC", "alice");
        var t2 = await svc.TrustDeviceAsync(u.Id, "PC", "alice");
        t1.DeviceToken.Should().NotBe(t2.DeviceToken);
    }

    [Fact]
    public async Task AutoLogin_for_locked_account_fails_with_AccountLocked()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var trust = await svc.TrustDeviceAsync(u.Id, "PC", "alice");
        u.IsLocked = true;
        await db.Db.SaveChangesAsync();
        var result = await svc.AutoLoginAsync("PC", "alice", trust.DeviceToken, clientIp: null);
        result.Failure.Should().Be(LoginFailureReason.AccountLocked);
    }

    [Fact]
    public async Task Login_succeeds_records_client_ip_on_refresh_token()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: "10.0.0.42");
        login.Failure.Should().BeNull();
        var tok = await db.Db.RefreshTokens.AsNoTracking().FirstAsync(t => t.Id == login.Response!.RefreshTokenId);
        tok.ClientIp.Should().Be("10.0.0.42");
    }

    // ===== ログイン時の Windows ユーザー / 端末の監査 =====

    [Fact]
    public async Task Login_with_mismatched_windows_user_records_identity_mismatch_audit()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);

        // Windows ユーザー "bob" が Watashi ユーザー "alice" でログイン。ログイン自体は成功する。
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: "10.0.0.1",
            windowsUsername: "bob", machineName: "PC1");
        login.Failure.Should().BeNull();

        db.Db.ChangeTracker.Clear();
        var audits = await db.Db.AuditLogs.AsNoTracking()
            .Where(a => a.Operation == AuthOperations.LoginIdentityMismatch).ToListAsync();
        audits.Should().HaveCount(1);
        audits[0].Username.Should().Be("alice");
        audits[0].Result.Should().Be(AuditResults.Warning);
        audits[0].ClientHostname.Should().Be("PC1");

        var user = await db.Db.Users.AsNoTracking().FirstAsync(u => u.Username == "alice");
        user.LastWindowsUsername.Should().Be("bob");
        user.LastMachineName.Should().Be("PC1");
    }

    [Fact]
    public async Task Login_with_matching_windows_user_records_no_mismatch_audit()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);

        await svc.LoginAsync("alice", "Admin123!@#", clientIp: null,
            windowsUsername: "ALICE", machineName: "PC1"); // 大文字小文字は無視

        db.Db.ChangeTracker.Clear();
        var mismatches = await db.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Operation == AuthOperations.LoginIdentityMismatch);
        mismatches.Should().Be(0);
    }

    [Fact]
    public async Task Login_from_new_machine_records_device_change_only_after_first()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);

        // 初回は前回値が無いので端末変更は記録しない (LastMachineName をセットするだけ)。
        await svc.LoginAsync("alice", "Admin123!@#", clientIp: null, windowsUsername: "alice", machineName: "PC1");
        // 別マシンからの2回目で端末変更を記録する。
        await svc.LoginAsync("alice", "Admin123!@#", clientIp: null, windowsUsername: "alice", machineName: "PC2");

        db.Db.ChangeTracker.Clear();
        var changes = await db.Db.AuditLogs.AsNoTracking()
            .Where(a => a.Operation == AuthOperations.LoginDeviceChanged).ToListAsync();
        changes.Should().HaveCount(1);
        changes[0].Path.Should().Contain("PC1").And.Contain("PC2");

        var user = await db.Db.Users.AsNoTracking().FirstAsync(u => u.Username == "alice");
        user.LastMachineName.Should().Be("PC2");
    }

    [Fact]
    public async Task Login_without_client_identity_records_no_audit()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);

        // Windows 情報を申告しない場合は注意イベントを残さない。
        await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);

        db.Db.ChangeTracker.Clear();
        var count = await db.Db.AuditLogs.AsNoTracking().CountAsync(a =>
            a.Operation == AuthOperations.LoginIdentityMismatch ||
            a.Operation == AuthOperations.LoginDeviceChanged);
        count.Should().Be(0);
    }

    [Fact]
    public async Task AutoLogin_with_mismatched_windows_user_records_identity_mismatch_audit()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        // alice の信頼デバイスを Windows ユーザー "bob" で登録 (運用外の状態を再現)。
        var trust = await svc.TrustDeviceAsync(u.Id, "PC9", "bob");

        var result = await svc.AutoLoginAsync("PC9", "bob", trust.DeviceToken, clientIp: null);
        result.Failure.Should().BeNull();

        db.Db.ChangeTracker.Clear();
        var audits = await db.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Operation == AuthOperations.LoginIdentityMismatch);
        audits.Should().Be(1);
    }
}
