using FluentAssertions;
using Watashi.Server.Auth;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public class UserAuthorizationVersionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Role_change_advances_the_credential_version(bool currentRole, bool newRole)
    {
        var previous = DateTime.UtcNow.AddMinutes(-1);
        var changedAt = DateTime.UtcNow;
        var user = new User { IsAdmin = currentRole, PasswordChangedAt = previous };

        UserAuthorizationVersion.ApplyAdminRole(user, newRole, changedAt).Should().BeTrue();

        user.IsAdmin.Should().Be(newRole);
        user.PasswordChangedAt.Should().Be(changedAt);
    }

    [Fact]
    public void Unchanged_role_keeps_the_existing_credential_version()
    {
        var previous = DateTime.UtcNow.AddMinutes(-1);
        var user = new User { IsAdmin = true, PasswordChangedAt = previous };

        UserAuthorizationVersion.ApplyAdminRole(user, true, DateTime.UtcNow).Should().BeFalse();

        user.PasswordChangedAt.Should().Be(previous);
    }
}
