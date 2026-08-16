using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public class BootstrapAdminCommandTests
{
    [Fact]
    public async Task Data_seeder_does_not_create_a_known_initial_user()
    {
        using var db = new TestDb();

        await DataSeeder.SeedAsync(db.Db);

        (await db.Db.Users.CountAsync()).Should().Be(0);
        (await db.Db.SystemSettings.AnyAsync()).Should().BeTrue();
        (await db.Db.PermissionTemplates.AnyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Bootstrap_creates_random_admin_and_requires_password_change()
    {
        using var db = new TestDb();

        var result = await BootstrapAdminCommand.ExecuteAsync(db.Db);

        result.Succeeded.Should().BeTrue();
        result.Username.Should().Be("admin");
        result.OneTimePassword.Should().NotBeNullOrWhiteSpace();
        PasswordPolicy.Validate(result.OneTimePassword!).ok.Should().BeTrue();

        var user = await db.Db.Users.SingleAsync();
        user.IsAdmin.Should().BeTrue();
        user.MustChangePassword.Should().BeTrue();
        user.IsPasswordSetupPending.Should().BeFalse();
        user.LastLoginAt.Should().BeNull();
        BCrypt.Net.BCrypt.Verify(result.OneTimePassword, user.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task Rerun_before_first_login_rotates_unseen_password()
    {
        using var db = new TestDb();
        var first = await BootstrapAdminCommand.ExecuteAsync(db.Db);
        var firstHash = (await db.Db.Users.SingleAsync()).PasswordHash;

        var second = await BootstrapAdminCommand.ExecuteAsync(db.Db);

        second.Succeeded.Should().BeTrue();
        second.WasReset.Should().BeTrue();
        second.OneTimePassword.Should().NotBe(first.OneTimePassword);
        var user = await db.Db.Users.SingleAsync();
        user.PasswordHash.Should().NotBe(firstHash);
        BCrypt.Net.BCrypt.Verify(first.OneTimePassword!, user.PasswordHash).Should().BeFalse();
        BCrypt.Net.BCrypt.Verify(second.OneTimePassword!, user.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task Bootstrap_is_refused_after_admin_has_logged_in()
    {
        using var db = new TestDb();
        var initial = await BootstrapAdminCommand.ExecuteAsync(db.Db);
        var user = await db.Db.Users.SingleAsync();
        user.LastLoginAt = DateTime.UtcNow;
        await db.Db.SaveChangesAsync();
        var hashBefore = user.PasswordHash;

        var retry = await BootstrapAdminCommand.ExecuteAsync(db.Db);

        retry.Succeeded.Should().BeFalse();
        retry.OneTimePassword.Should().BeNull();
        (await db.Db.Users.SingleAsync()).PasswordHash.Should().Be(hashBefore);
        BCrypt.Net.BCrypt.Verify(initial.OneTimePassword!, hashBefore).Should().BeTrue();
    }

    [Fact]
    public async Task Bootstrap_recovers_when_there_is_no_usable_admin_without_promoting_existing_user()
    {
        using var db = new TestDb();
        db.Db.Users.Add(new User
        {
            Username = "admin",
            PasswordHash = PasswordSetup.CreateUnusableHash(),
            IsAdmin = false,
            IsPasswordSetupPending = true,
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(90),
            CreatedAt = DateTime.UtcNow,
        });
        await db.Db.SaveChangesAsync();

        var result = await BootstrapAdminCommand.ExecuteAsync(db.Db);

        result.Succeeded.Should().BeTrue();
        result.Username.Should().Be("bootstrap-admin");
        (await db.Db.Users.SingleAsync(u => u.Username == "admin")).IsAdmin.Should().BeFalse();
        (await db.Db.Users.SingleAsync(u => u.Username == "bootstrap-admin")).IsAdmin.Should().BeTrue();
    }
}
