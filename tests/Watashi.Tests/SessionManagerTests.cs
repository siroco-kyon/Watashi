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

    [Fact]
    public async Task Expiry_for_a_previous_access_token_does_not_clear_the_new_login()
    {
        var session = new SessionManager();
        session.SetFromLogin(Login("same-access", "old-refresh-id", "old-refresh"));
        var oldSession = await session.GetValidAccessTokenSnapshotAsync();
        session.SetFromLogin(Login("same-access", "new-refresh-id", "new-refresh"));
        var count = 0;
        session.SessionExpired += _ => count++;

        session.ExpireSession(
            "delayed_401",
            oldSession.AccessToken,
            oldSession.SessionGeneration);

        session.IsAuthenticated.Should().BeTrue();
        session.CurrentAccessToken.Should().Be("same-access");
        session.RefreshTokenId.Should().Be("new-refresh-id");
        count.Should().Be(0);
        session.Clear();
    }

    [Fact]
    public async Task Delayed_refresh_response_does_not_overwrite_a_new_login()
    {
        var session = new SessionManager();
        session.SetFromLogin(Login("old-access", "old-refresh-id", "old-refresh", expiresIn: 0));
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshResponse = new TaskCompletionSource<RefreshResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RefreshDelegate = (_, _, _) =>
        {
            refreshStarted.TrySetResult();
            return refreshResponse.Task;
        };

        var oldRefresh = session.GetValidAccessTokenAsync();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.SetFromLogin(Login("new-access", "new-refresh-id", "new-refresh"));
        refreshResponse.SetResult(new RefreshResponse
        {
            AccessToken = "stale-refreshed-access",
            RefreshTokenId = "stale-refreshed-id",
            RefreshToken = "stale-refreshed-token",
            ExpiresIn = 900,
        });

        (await oldRefresh).Should().Be("new-access");
        session.CurrentAccessToken.Should().Be("new-access");
        session.RefreshTokenId.Should().Be("new-refresh-id");
        session.RefreshToken.Should().Be("new-refresh");
        session.Clear();
    }

    [Fact]
    public async Task Logout_snapshot_uses_the_refresh_token_returned_by_rotation()
    {
        var session = new SessionManager();
        session.SetFromLogin(Login("old-access", "old-refresh-id", "old-refresh", expiresIn: 0));
        session.RefreshDelegate = (_, _, _) => Task.FromResult(new RefreshResponse
        {
            AccessToken = "new-access",
            RefreshTokenId = "new-refresh-id",
            RefreshToken = "new-refresh",
            ExpiresIn = 900,
        });

        var logout = await session.GetRefreshTokenForLogoutAsync();

        logout.Should().NotBeNull();
        logout!.Value.RefreshTokenId.Should().Be("new-refresh-id");
        logout.Value.RefreshToken.Should().Be("new-refresh");
        session.Clear();
    }

    [Fact]
    public async Task Refresh_notifies_when_password_change_becomes_mandatory()
    {
        var session = new SessionManager();
        session.SetFromLogin(Login(expiresIn: 0));
        var notifications = 0;
        session.RefreshNeedsPasswordChange += () => notifications++;
        session.RefreshDelegate = (_, _, _) => Task.FromResult(new RefreshResponse
        {
            AccessToken = "must-change-access",
            RefreshTokenId = "new-refresh-id",
            RefreshToken = "new-refresh",
            ExpiresIn = 900,
            MustChangePassword = true,
        });

        await session.GetValidAccessTokenAsync();

        session.MustChangePassword.Should().BeTrue();
        notifications.Should().Be(1);
        session.Clear();
    }

    private static LoginResponse Login(
        string accessToken = "not-a-jwt",
        string refreshTokenId = "refresh-id",
        string refreshToken = "refresh-token",
        int expiresIn = 900) => new()
        {
            AccessToken = accessToken,
            RefreshTokenId = refreshTokenId,
            RefreshToken = refreshToken,
            ExpiresIn = expiresIn,
            IdleMinutes = 30,
        };
}
