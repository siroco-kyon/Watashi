using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Auth;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

/// <summary>
/// 管理者による明示無効化は、自動ロックとは独立したまま、全ての認証経路を即時に閉じる。
/// </summary>
public class UserAccountDisableTests
{
    private const string Password = "Admin123!@#";

    private static AuthService Build(TestDb db) => new(db.Db, new AuthServiceOptions
    {
        Secret = "TEST-SECRET-At-Least-32-Bytes-Long-XXXXXXXXXXXXXXX",
        Issuer = "Watashi",
        Audience = "Watashi",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30,
    });

    private static User User(string username, bool admin = false, bool disabled = false)
    {
        var now = DateTime.UtcNow.AddMinutes(-5);
        return new User
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            IsAdmin = admin,
            IsDisabled = disabled,
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(30),
            CreatedAt = now,
        };
    }

    [Fact]
    public async Task Disable_records_evidence_and_revokes_every_authentication_mechanism()
    {
        using var db = new TestDb();
        var actor = User("admin", admin: true);
        var target = User("alice");
        target.IsLocked = true;
        target.FailedLoginCount = 4;
        db.Db.Users.AddRange(actor, target);
        await db.Db.SaveChangesAsync();

        var issuedAt = DateTime.UtcNow.AddMinutes(-2);
        var device = new TrustedDevice
        {
            UserId = target.Id,
            MachineName = "PC-01",
            WindowsUsername = "alice",
            DeviceTokenHash = "device-hash",
            RegisteredAt = issuedAt,
            LastUsedAt = issuedAt,
        };
        db.Db.TrustedDevices.Add(device);
        db.Db.RefreshTokens.Add(new RefreshToken
        {
            Id = "refresh-1",
            UserId = target.Id,
            TokenHash = "refresh-hash",
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt.AddDays(30),
            LastUsedAt = issuedAt,
        });
        await db.Db.SaveChangesAsync();

        var changedAt = DateTime.UtcNow;
        await UserAccountLifecycle.DisableAsync(
            db.Db, target, actor.Id, actor.Username, "退職のため", changedAt);

        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id);
        fresh.IsDisabled.Should().BeTrue();
        fresh.DisabledAt.Should().Be(changedAt);
        fresh.DisabledReason.Should().Be("退職のため");
        fresh.DisabledByUserId.Should().Be(actor.Id);
        fresh.DisabledByUsername.Should().Be("admin");
        fresh.PasswordChangedAt.Should().Be(changedAt);
        // 自動ロックは別状態なので無効化操作で変更しない。
        fresh.IsLocked.Should().BeTrue();
        fresh.FailedLoginCount.Should().Be(4);

        var refresh = await db.Db.RefreshTokens.AsNoTracking().SingleAsync();
        refresh.IsRevoked.Should().BeTrue();
        refresh.LastUsedAt.Should().Be(changedAt);
        var trusted = await db.Db.TrustedDevices.AsNoTracking().SingleAsync();
        trusted.IsRevoked.Should().BeTrue();
        trusted.RevokedAt.Should().Be(changedAt);
        trusted.RevokedReason.Should().Be("account_disabled");
    }

    [Fact]
    public async Task Enable_keeps_last_disable_evidence_and_revokes_credentials_again()
    {
        using var db = new TestDb();
        var actor = User("admin", admin: true);
        var target = User("alice");
        db.Db.Users.AddRange(actor, target);
        await db.Db.SaveChangesAsync();

        var disabledAt = DateTime.UtcNow.AddMinutes(-1);
        await UserAccountLifecycle.DisableAsync(
            db.Db, target, actor.Id, actor.Username, "長期休職", disabledAt);

        // 無効化と競合して認証情報が残った状況を再現する。再有効化側でも必ず全失効する。
        var raceAt = disabledAt.AddSeconds(10);
        db.Db.RefreshTokens.Add(new RefreshToken
        {
            Id = "race-refresh",
            UserId = target.Id,
            TokenHash = "hash",
            IssuedAt = raceAt,
            ExpiresAt = raceAt.AddDays(30),
            LastUsedAt = raceAt,
        });
        db.Db.TrustedDevices.Add(new TrustedDevice
        {
            UserId = target.Id,
            MachineName = "PC-RACE",
            WindowsUsername = "alice",
            DeviceTokenHash = "hash",
            RegisteredAt = raceAt,
            LastUsedAt = raceAt,
        });
        await db.Db.SaveChangesAsync();

        var enabledAt = disabledAt.AddMinutes(2);
        await UserAccountLifecycle.EnableAsync(db.Db, target, enabledAt);

        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.AsNoTracking().SingleAsync(u => u.Id == target.Id);
        fresh.IsDisabled.Should().BeFalse();
        fresh.PasswordChangedAt.Should().Be(enabledAt);
        fresh.DisabledAt.Should().Be(disabledAt);
        fresh.DisabledReason.Should().Be("長期休職");
        fresh.DisabledByUserId.Should().Be(actor.Id);
        fresh.DisabledByUsername.Should().Be("admin");

        (await db.Db.RefreshTokens.AsNoTracking().SingleAsync()).IsRevoked.Should().BeTrue();
        var device = await db.Db.TrustedDevices.AsNoTracking().SingleAsync();
        device.IsRevoked.Should().BeTrue();
        device.RevokedAt.Should().Be(enabledAt);
        device.RevokedReason.Should().Be("account_reenabled");
    }

    [Fact]
    public async Task Disabled_user_cannot_log_in_and_failed_count_does_not_increase()
    {
        using var db = new TestDb();
        var user = User("alice", disabled: true);
        user.FailedLoginCount = 7;
        db.Db.Users.Add(user);
        await db.Db.SaveChangesAsync();

        var result = await Build(db).LoginAsync("alice", Password, "10.0.0.1", machineName: "PC-01");

        result.Response.Should().BeNull();
        result.Failure.Should().Be(LoginFailureReason.AccountDisabled);
        user.FailedLoginCount.Should().Be(7);
        var audit = await db.Db.AuditLogs.AsNoTracking().SingleAsync();
        audit.Operation.Should().Be(AuthOperations.LoginFailed);
        audit.ErrorMessage.Should().Be("account_disabled");
    }

    [Fact]
    public async Task Disabled_user_cannot_auto_login_even_with_an_active_device()
    {
        using var db = new TestDb();
        var user = User("alice");
        db.Db.Users.Add(user);
        await db.Db.SaveChangesAsync();
        var service = Build(db);
        var trusted = await service.TrustDeviceAsync(user.Id, "PC-01", "alice");
        user.IsDisabled = true;
        await db.Db.SaveChangesAsync();

        var result = await service.AutoLoginAsync("PC-01", "alice", trusted.DeviceToken, "10.0.0.1");

        result.Response.Should().BeNull();
        result.Failure.Should().Be(LoginFailureReason.AccountDisabled);
        (await db.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Operation == AuthOperations.LoginFailed))
            .ErrorMessage.Should().Be("account_disabled");
    }

    [Fact]
    public async Task Refresh_reports_account_disabled_before_generic_revocation_errors()
    {
        using var db = new TestDb();
        var user = User("alice");
        db.Db.Users.Add(user);
        await db.Db.SaveChangesAsync();
        var service = Build(db);
        var login = await service.LoginAsync("alice", Password, clientIp: null);
        user.IsDisabled = true;
        await db.Db.SaveChangesAsync();

        var (response, error) = await service.RefreshAsync(
            login.Response!.RefreshTokenId, login.Response.RefreshToken);

        response.Should().BeNull();
        error.Should().Be("account_disabled");
    }

    [Fact]
    public async Task Disabled_pending_user_is_not_eligible_for_password_setup()
    {
        using var db = new TestDb();
        var user = User("G012345", disabled: true);
        user.IsPasswordSetupPending = true;
        user.PasswordHash = PasswordSetup.CreateUnusableHash();
        db.Db.Users.Add(user);
        await db.Db.SaveChangesAsync();

        var (eligible, _) = await Build(db).PreparePasswordSetupAsync(
            "G012345", @"CORP\G012345",
            new WindowsAuthOptions
            {
                Mode = WindowsAuthModes.Negotiate,
                DomainMatch = WindowsAuthDomainMatchModes.IgnoreDomain,
            }, null, null);

        eligible.Should().BeFalse();
    }
}
