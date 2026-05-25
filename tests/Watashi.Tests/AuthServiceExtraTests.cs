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

    private static async Task<User> SeedUserAsync(TestDb db, int idleMinutes = 30, int passwordExpiryDays = 90)
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
}
