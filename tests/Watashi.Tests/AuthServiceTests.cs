using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

public class AuthServiceTests
{
    private static AuthService Build(TestDb db)
    {
        var opts = new AuthServiceOptions
        {
            Secret = "TEST-SECRET-At-Least-32-Bytes-Long-XXXXXXXXXXXXXXX",
            Issuer = "Watashi", Audience = "Watashi",
            AccessTokenMinutes = 15, RefreshTokenDays = 30,
        };
        return new AuthService(db.Db, opts);
    }

    private static async Task<User> SeedUserAsync(TestDb db, string pw = "Admin123!@#", bool admin = false)
    {
        var u = new User
        {
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(pw),
            IsAdmin = admin,
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
        };
        db.Db.Users.Add(u);
        db.Db.SystemSettings.Add(new SystemSetting { Key = "PasswordExpiryDays", Value = "90", UpdatedAt = DateTime.UtcNow });
        await db.Db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task Login_with_correct_password_succeeds()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var result = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        result.Failure.Should().BeNull();
        result.Response.Should().NotBeNull();
        result.Response!.AccessToken.Should().NotBeEmpty();
        result.Response.RefreshToken.Should().NotBeEmpty();
        result.Response.RefreshTokenId.Should().NotBeEmpty();
        result.Response.ExpiresIn.Should().Be(900);
    }

    [Fact]
    public async Task Login_with_wrong_password_increments_failure_counter()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        await svc.LoginAsync("alice", "wrong", clientIp: null);
        var fresh = await db.Db.Users.FindAsync(user.Id);
        fresh!.FailedLoginCount.Should().Be(1);
    }

    [Fact]
    public async Task Five_failed_logins_locks_account()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        for (int i = 0; i < 5; i++)
            await svc.LoginAsync("alice", "wrong", clientIp: null);
        var fresh = await db.Db.Users.FindAsync(user.Id);
        fresh!.IsLocked.Should().BeTrue();

        var locked = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        locked.Failure.Should().Be(LoginFailureReason.AccountLocked);
    }

    [Fact]
    public async Task Refresh_with_valid_token_returns_new_access_token()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);

        var (res, err) = await svc.RefreshAsync(login.Response!.RefreshTokenId, login.Response.RefreshToken);
        err.Should().BeNull();
        res.Should().NotBeNull();
        res!.AccessToken.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Refresh_after_logout_fails()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        await svc.LogoutAsync(login.Response!.RefreshTokenId);

        var (res, err) = await svc.RefreshAsync(login.Response.RefreshTokenId, login.Response.RefreshToken);
        res.Should().BeNull();
        // 失効済みトークンの再提示は再利用検知として扱う（ファミリー失効）。
        err.Should().Be("token_reuse_detected");
    }

    [Fact]
    public async Task Refresh_for_locked_user_fails()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        user.IsLocked = true;
        await db.Db.SaveChangesAsync();

        var (res, err) = await svc.RefreshAsync(login.Response!.RefreshTokenId, login.Response.RefreshToken);
        res.Should().BeNull();
        err.Should().Be("account_locked");
    }

    [Fact]
    public async Task ChangePassword_with_weak_password_is_rejected()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var (ok, _) = await svc.ChangePasswordAsync(u.Id, "Admin123!@#", "short");
        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePassword_resets_mustChangePassword_and_extends_expiry()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        u.MustChangePassword = true;
        u.PasswordExpiresAt = DateTime.UtcNow.AddDays(-1); // expired
        await db.Db.SaveChangesAsync();
        var svc = Build(db);
        var (ok, _) = await svc.ChangePasswordAsync(u.Id, "Admin123!@#", "NewStrongPassword2026!");
        ok.Should().BeTrue();
        var fresh = await db.Db.Users.FindAsync(u.Id);
        fresh!.MustChangePassword.Should().BeFalse();
        fresh.PasswordExpiresAt.Should().BeAfter(DateTime.UtcNow.AddDays(30));
    }

    [Fact]
    public async Task TrustDevice_replaces_existing_device_for_same_user()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var first = await svc.TrustDeviceAsync(u.Id, "PC01", "alice");
        var second = await svc.TrustDeviceAsync(u.Id, "PC02", "alice");
        first.DeviceToken.Should().NotBe(second.DeviceToken);
        db.Db.TrustedDevices.Count(d => d.UserId == u.Id).Should().Be(1);
    }

    [Fact]
    public async Task AutoLogin_with_correct_device_token_succeeds()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var trust = await svc.TrustDeviceAsync(u.Id, "PC01", "alice");
        var result = await svc.AutoLoginAsync("PC01", "alice", trust.DeviceToken, clientIp: null);
        result.Failure.Should().BeNull();
        result.Response.Should().NotBeNull();
    }

    [Fact]
    public async Task AutoLogin_with_wrong_token_fails()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        await svc.TrustDeviceAsync(u.Id, "PC01", "alice");
        var result = await svc.AutoLoginAsync("PC01", "alice", "WRONG-TOKEN", clientIp: null);
        result.Failure.Should().Be(LoginFailureReason.InvalidCredentials);
    }
}
