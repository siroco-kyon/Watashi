using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

public class AuthenticationLifecycleAuditTests
{
    private const string CurrentPassword = "Admin123!@#";
    private const string NewPassword = "NewStrongPassword2026!";

    [Theory]
    [InlineData(AuthOperations.LoginSucceeded, "ログイン成功")]
    [InlineData(AuthOperations.Logout, "ログアウト")]
    [InlineData(AuthOperations.RefreshSucceeded, "セッション更新成功")]
    [InlineData(AuthOperations.RefreshReuseRejected, "更新トークン再利用拒否")]
    [InlineData(AuthOperations.TrustedDeviceRegistered, "信頼済み端末登録")]
    [InlineData(AuthOperations.TrustedDeviceRevoked, "信頼済み端末失効")]
    [InlineData(AuthOperations.PasswordChanged, "パスワード変更")]
    public void Lifecycle_operations_are_exposed_as_auth_filters(string operation, string label)
    {
        AuditLogFilterValues.CategoryFor(operation).Should().Be(AuditLogFilterValues.AuthCategory);
        AuditLogDto.FormatOperation(operation).Should().Be(label);
        AuditLogFilterValues.OperationOptions.Should().Contain(option =>
            option.Value == operation && option.Label == label && option.Category == AuditLogFilterValues.AuthCategory);
    }

    [Fact]
    public async Task Password_login_success_records_client_and_never_records_credentials()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var service = Build(db);

        var result = await service.LoginAsync(
            user.Username, CurrentPassword, "10.10.0.5", "alice", "CLIENT-01");

        result.Response.Should().NotBeNull();
        var audit = await SingleAuditAsync(db, AuthOperations.LoginSucceeded);
        audit.UserId.Should().Be(user.Id);
        audit.Username.Should().Be(user.Username);
        audit.Result.Should().Be(AuditResults.Success);
        audit.ErrorMessage.Should().Be("password_authenticated");
        audit.ClientIp.Should().Be("10.10.0.5");
        audit.ClientHostname.Should().Be("CLIENT-01");
        AssertContainsNoCredential(audit, CurrentPassword,
            result.Response!.AccessToken, result.Response.RefreshToken, result.Response.RefreshTokenId);
    }

    [Fact]
    public async Task Refresh_rotation_and_reuse_rejection_are_audited_without_tokens()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var service = Build(db);
        var login = (await service.LoginAsync("alice", CurrentPassword, clientIp: null)).Response!;

        var (rotated, error) = await service.RefreshAsync(
            login.RefreshTokenId, login.RefreshToken, "10.10.0.6", "CLIENT-02");
        error.Should().BeNull();
        rotated.Should().NotBeNull();

        var (reused, reuseError) = await service.RefreshAsync(
            login.RefreshTokenId, login.RefreshToken, "10.10.0.7", "CLIENT-03");
        reused.Should().BeNull();
        reuseError.Should().Be("token_reuse_detected");

        var success = await SingleAuditAsync(db, AuthOperations.RefreshSucceeded);
        success.Result.Should().Be(AuditResults.Success);
        success.ErrorMessage.Should().Be("token_rotated");
        success.ClientIp.Should().Be("10.10.0.6");
        success.ClientHostname.Should().Be("CLIENT-02");

        var rejected = await SingleAuditAsync(db, AuthOperations.RefreshReuseRejected);
        rejected.Result.Should().Be(AuditResults.Warning);
        rejected.ErrorMessage.Should().Be("token_reuse_detected");
        rejected.ClientIp.Should().Be("10.10.0.7");
        rejected.ClientHostname.Should().Be("CLIENT-03");

        AssertContainsNoCredential(success, login.RefreshToken, login.RefreshTokenId,
            rotated!.RefreshToken, rotated.RefreshTokenId);
        AssertContainsNoCredential(rejected, login.RefreshToken, login.RefreshTokenId,
            rotated.RefreshToken, rotated.RefreshTokenId);
    }

    [Fact]
    public async Task Logout_success_and_rejection_are_audited_without_token_value()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var service = Build(db);
        var login = (await service.LoginAsync(user.Username, CurrentPassword, clientIp: null)).Response!;

        var rejected = await service.LogoutAsync(user.Id, login.RefreshTokenId, "wrong-token",
            "10.10.0.8", "CLIENT-04", user.Username);
        rejected.Should().BeFalse();

        var succeeded = await service.LogoutAsync(user.Id, login.RefreshTokenId, login.RefreshToken,
            "10.10.0.8", "CLIENT-04", user.Username);
        succeeded.Should().BeTrue();

        var audits = await db.Db.AuditLogs.AsNoTracking()
            .Where(a => a.Operation == AuthOperations.Logout)
            .OrderBy(a => a.Id)
            .ToListAsync();
        audits.Should().HaveCount(2);
        audits[0].Result.Should().Be(AuditResults.Failure);
        audits[0].ErrorMessage.Should().Be("token_invalid");
        audits[1].Result.Should().Be(AuditResults.Success);
        audits[1].ErrorMessage.Should().Be("session_revoked");
        audits.Should().OnlyContain(a => a.ClientIp == "10.10.0.8" && a.ClientHostname == "CLIENT-04");
        audits.ForEach(a => AssertContainsNoCredential(a, "wrong-token", login.RefreshToken, login.RefreshTokenId));
    }

    [Fact]
    public async Task Trusted_device_registration_and_replacement_revocation_are_audited_without_device_tokens()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var service = Build(db);

        var first = await service.TrustDeviceAsync(user.Id, "CLIENT-05", "alice", "10.10.0.9");
        var second = await service.TrustDeviceAsync(user.Id, "CLIENT-05", "alice", "10.10.0.10");

        var registrations = await db.Db.AuditLogs.AsNoTracking()
            .Where(a => a.Operation == AuthOperations.TrustedDeviceRegistered)
            .OrderBy(a => a.Id)
            .ToListAsync();
        registrations.Should().HaveCount(2);
        registrations.Should().OnlyContain(a =>
            a.Result == AuditResults.Success && a.ErrorMessage == "device_registered");

        var revocation = await SingleAuditAsync(db, AuthOperations.TrustedDeviceRevoked);
        revocation.Result.Should().Be(AuditResults.Success);
        revocation.ErrorMessage.Should().Be("device_re_registered");
        revocation.ClientIp.Should().Be("10.10.0.10");
        revocation.ClientHostname.Should().Be("CLIENT-05");

        registrations.ForEach(a => AssertContainsNoCredential(a, first.DeviceToken, second.DeviceToken));
        AssertContainsNoCredential(revocation, first.DeviceToken, second.DeviceToken);
    }

    [Fact]
    public async Task Password_change_success_and_failure_use_safe_reason_codes_without_passwords()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var service = Build(db);

        var (failed, _) = await service.ChangePasswordAsync(user.Id,
            "wrong-current-password", NewPassword, "10.10.0.11", "CLIENT-07");
        failed.Should().BeNull();

        var (changed, error) = await service.ChangePasswordAsync(user.Id,
            CurrentPassword, NewPassword, "10.10.0.11", "CLIENT-07");
        error.Should().BeNull();
        changed.Should().NotBeNull();

        var audits = await db.Db.AuditLogs.AsNoTracking()
            .Where(a => a.Operation == AuthOperations.PasswordChanged)
            .OrderBy(a => a.Id)
            .ToListAsync();
        audits.Should().HaveCount(2);
        audits[0].Result.Should().Be(AuditResults.Failure);
        audits[0].ErrorMessage.Should().Be("current_password_invalid");
        audits[1].Result.Should().Be(AuditResults.Success);
        audits[1].ErrorMessage.Should().Be("password_changed");
        audits.Should().OnlyContain(a => a.ClientIp == "10.10.0.11" && a.ClientHostname == "CLIENT-07");
        audits.ForEach(a => AssertContainsNoCredential(
            a, CurrentPassword, NewPassword, "wrong-current-password", changed!.RefreshToken));
    }

    [Fact]
    public async Task Audit_insert_failure_does_not_turn_successful_login_into_failure()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        await db.Db.Database.ExecuteSqlRawAsync($$"""
            CREATE TRIGGER fail_login_success_audit
            BEFORE INSERT ON AuditLogs
            WHEN NEW.Operation = '{{AuthOperations.LoginSucceeded}}'
            BEGIN
                SELECT RAISE(ABORT, 'audit sink unavailable');
            END;
            """);
        var service = Build(db);

        var result = await service.LoginAsync(
            user.Username, CurrentPassword, "10.10.0.12", "alice", "CLIENT-08");

        result.Failure.Should().BeNull();
        result.Response.Should().NotBeNull();
        db.Db.ChangeTracker.Clear();
        (await db.Db.RefreshTokens.AsNoTracking()
            .CountAsync(t => t.UserId == user.Id && !t.IsRevoked)).Should().Be(1);
        (await db.Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id))
            .LastLoginAt.Should().NotBeNull();
        (await db.Db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.Operation == AuthOperations.LoginSucceeded)).Should().Be(0);
        var queued = await db.Db.AuditOutboxEntries.AsNoTracking().SingleAsync();
        queued.AttemptCount.Should().Be(0);

        await db.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_login_success_audit");
        var dispatched = await AuditOutboxDispatcher.DrainOnceAsync(
            db.Db, queued.NextAttemptAt.AddSeconds(1));
        dispatched.Should().Be(1);
        (await db.Db.AuditOutboxEntries.CountAsync()).Should().Be(0);
        var persisted = await db.Db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Operation == AuthOperations.LoginSucceeded);
        persisted.EventId.Should().Be(queued.EventId);
    }

    [Fact]
    public async Task Authentication_audit_rejects_free_form_reason_text()
    {
        using var db = new TestDb();
        var audit = new AuditLogService(db.Db);

        var written = await audit.TryLogAuthenticationAsync(
            userId: null,
            username: "alice",
            operation: AuthOperations.LoginFailed,
            result: AuditResults.Failure,
            reasonCode: "Password=DoNotStoreThis!",
            clientIp: "10.10.0.13",
            clientHostname: "CLIENT-09");

        written.Should().BeTrue();
        var row = await SingleAuditAsync(db, AuthOperations.LoginFailed);
        row.ErrorMessage.Should().Be("unspecified");
        AssertContainsNoCredential(row, "DoNotStoreThis", "Password=DoNotStoreThis!");
    }

    private static AuthService Build(TestDb db)
        => new(db.Db, new AuthServiceOptions
        {
            Secret = "TEST-SECRET-At-Least-32-Bytes-Long-XXXXXXXXXXXXXXX",
            Issuer = "Watashi",
            Audience = "Watashi",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30,
        });

    private static async Task<User> SeedUserAsync(TestDb db)
    {
        var user = new User
        {
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(CurrentPassword),
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
        };
        db.Db.Users.Add(user);
        db.Db.SystemSettings.Add(new SystemSetting
        {
            Key = SettingKeys.PasswordExpiryDays,
            Value = "90",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.Db.SaveChangesAsync();
        return user;
    }

    private static Task<AuditLog> SingleAuditAsync(TestDb db, string operation)
        => db.Db.AuditLogs.AsNoTracking().SingleAsync(a => a.Operation == operation);

    private static void AssertContainsNoCredential(AuditLog audit, params string[] credentials)
    {
        var persistedText = string.Join("\n", new[]
        {
            audit.Username,
            audit.Operation,
            audit.Path,
            audit.TargetPath,
            audit.Result,
            audit.ErrorMessage,
            audit.ClientIp,
            audit.ClientHostname,
            audit.Protocol,
        }.Where(value => value is not null));

        foreach (var credential in credentials.Where(value => !string.IsNullOrEmpty(value)))
            persistedText.Should().NotContain(credential);
    }
}
