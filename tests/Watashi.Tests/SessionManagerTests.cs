using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Tests;

public class SessionManagerTests
{
    [Fact]
    public void ExpireSession_clears_state_and_notifies_only_once()
    {
        var session = new SessionManager();
        session.SetFromLogin(Login());
        var reasons = new List<string?>();
        session.SessionExpired += reasons.Add;

        session.ExpireSession("credential_state_changed");
        session.ExpireSession("duplicate_401");

        session.IsAuthenticated.Should().BeFalse();
        session.CurrentAccessToken.Should().BeNull();
        reasons.Should().Equal("credential_state_changed");
    }

    [Fact]
    public void New_login_allows_a_later_expiry_notification()
    {
        var session = new SessionManager();
        var count = 0;
        session.SessionExpired += _ => count++;

        session.SetFromLogin(Login());
        session.ExpireSession("first");
        session.SetFromLogin(Login());
        session.ExpireSession("second");

        count.Should().Be(2);
    }

    private static LoginResponse Login() => new()
    {
        AccessToken = "not-a-jwt",
        RefreshTokenId = "refresh-id",
        RefreshToken = "refresh-token",
        ExpiresIn = 900,
        IdleMinutes = 30,
    };
}
