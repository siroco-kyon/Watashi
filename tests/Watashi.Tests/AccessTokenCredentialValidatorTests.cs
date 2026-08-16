using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Watashi.Server.Auth;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public class AccessTokenCredentialValidatorTests
{
    private const string Password = "Admin123!@#";

    private static AuthService Build(TestDb db)
        => new(db.Db, new AuthServiceOptions
        {
            Secret = "TEST-SECRET-At-Least-32-Bytes-Long-XXXXXXXXXXXXXXX",
            Issuer = "Watashi",
            Audience = "Watashi",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30,
        });

    private static ClaimsPrincipal Principal(
        string token, bool removeCredentialVersion = false, string? credentialVersion = null)
    {
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims;
        if (removeCredentialVersion || credentialVersion is not null)
            claims = claims.Where(c => c.Type != AuthClaims.CredentialVersion);
        if (credentialVersion is not null)
            claims = claims.Append(new Claim(AuthClaims.CredentialVersion, credentialVersion));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static async Task<User> SeedAsync(TestDb db)
    {
        var now = DateTime.UtcNow.AddMinutes(-1);
        var user = new User
        {
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(30),
            CreatedAt = now,
        };
        db.Db.Users.Add(user);
        await db.Db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Current_access_token_is_accepted()
    {
        using var db = new TestDb();
        await SeedAsync(db);
        var login = await Build(db).LoginAsync("alice", Password, clientIp: null);

        (await AccessTokenCredentialValidator.IsCurrentAsync(
            db.Db, Principal(login.Response!.AccessToken))).Should().BeTrue();
    }

    [Fact]
    public async Task Access_token_is_rejected_immediately_after_require_setup_state()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db);
        var login = await Build(db).LoginAsync("alice", Password, clientIp: null);

        user.IsPasswordSetupPending = true;
        user.PasswordChangedAt = DateTime.UtcNow;
        await db.Db.SaveChangesAsync();

        (await AccessTokenCredentialValidator.IsCurrentAsync(
            db.Db, Principal(login.Response!.AccessToken))).Should().BeFalse();
    }

    [Fact]
    public async Task Access_token_is_rejected_after_the_credential_version_changes()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db);
        var login = await Build(db).LoginAsync("alice", Password, clientIp: null);

        user.PasswordChangedAt = DateTime.UtcNow;
        await db.Db.SaveChangesAsync();

        (await AccessTokenCredentialValidator.IsCurrentAsync(
            db.Db, Principal(login.Response!.AccessToken))).Should().BeFalse();
    }

    [Fact]
    public async Task Access_token_is_rejected_while_the_account_is_locked()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db);
        var login = await Build(db).LoginAsync("alice", Password, clientIp: null);

        user.IsLocked = true;
        await db.Db.SaveChangesAsync();

        (await AccessTokenCredentialValidator.IsCurrentAsync(
            db.Db, Principal(login.Response!.AccessToken))).Should().BeFalse();
    }

    [Fact]
    public async Task Access_token_is_rejected_immediately_while_the_account_is_disabled()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db);
        var login = await Build(db).LoginAsync("alice", Password, clientIp: null);

        user.IsDisabled = true;
        await db.Db.SaveChangesAsync();

        (await AccessTokenCredentialValidator.IsCurrentAsync(
            db.Db, Principal(login.Response!.AccessToken))).Should().BeFalse();
    }

    [Fact]
    public async Task Access_token_is_rejected_immediately_after_an_admin_role_change()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db);
        var login = await Build(db).LoginAsync("alice", Password, clientIp: null);

        UserAuthorizationVersion.ApplyAdminRole(user, true, DateTime.UtcNow).Should().BeTrue();
        await db.Db.SaveChangesAsync();

        (await AccessTokenCredentialValidator.IsCurrentAsync(
            db.Db, Principal(login.Response!.AccessToken))).Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_access_token_uses_not_before_for_deployment_compatibility()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db);
        var login = await Build(db).LoginAsync("alice", Password, clientIp: null);
        var legacyPrincipal = Principal(login.Response!.AccessToken, removeCredentialVersion: true);

        (await AccessTokenCredentialValidator.IsCurrentAsync(db.Db, legacyPrincipal)).Should().BeTrue();

        user.PasswordChangedAt = DateTime.UtcNow.AddMinutes(1);
        await db.Db.SaveChangesAsync();

        (await AccessTokenCredentialValidator.IsCurrentAsync(db.Db, legacyPrincipal)).Should().BeFalse();
    }

    [Fact]
    public async Task Malformed_credential_version_is_fail_closed()
    {
        using var db = new TestDb();
        await SeedAsync(db);
        var login = await Build(db).LoginAsync("alice", Password, clientIp: null);

        (await AccessTokenCredentialValidator.IsCurrentAsync(
            db.Db, Principal(login.Response!.AccessToken, credentialVersion: "invalid"))).Should().BeFalse();
    }
}
