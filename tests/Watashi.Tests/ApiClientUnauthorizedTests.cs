using System.Net;
using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Tests;

public class ApiClientUnauthorizedTests
{
    [Fact]
    public async Task Authenticated_request_401_expires_the_local_session()
    {
        var session = AuthenticatedSession();
        var expired = 0;
        session.SessionExpired += _ => expired++;
        var api = ClientReturning(HttpStatusCode.Unauthorized, session);

        var request = async () => await api.GetHostsAsync();

        await request.Should().ThrowAsync<ApiException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.Unauthorized);
        session.IsAuthenticated.Should().BeFalse();
        expired.Should().Be(1);
    }

    [Fact]
    public async Task Anonymous_request_401_does_not_expire_an_existing_session()
    {
        var session = AuthenticatedSession();
        var expired = 0;
        session.SessionExpired += _ => expired++;
        var api = ClientReturning(HttpStatusCode.Unauthorized, session);

        var request = async () => await api.LoginAsync("alice", "wrong");

        await request.Should().ThrowAsync<ApiException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.Unauthorized);
        session.IsAuthenticated.Should().BeTrue();
        expired.Should().Be(0);
        session.Clear();
    }

    [Fact]
    public async Task Delayed_401_from_previous_login_does_not_expire_the_new_session()
    {
        // JWT には jti がないため、同一秒の再ログインでは token 文字列まで同じになり得る。
        var session = AuthenticatedSession("same-access", "old-refresh-id", "old-refresh");
        var expired = 0;
        session.SessionExpired += _ => expired++;
        var handler = new DeferredResponseHandler();
        var api = ClientUsing(handler, session);

        var oldRequest = api.GetHostsAsync();
        await handler.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.SetFromLogin(Login("same-access", "new-refresh-id", "new-refresh"));
        handler.Complete(HttpStatusCode.Unauthorized);

        var awaitOldRequest = async () => await oldRequest;
        await awaitOldRequest.Should().ThrowAsync<ApiException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.Unauthorized);
        session.IsAuthenticated.Should().BeTrue();
        session.CurrentAccessToken.Should().Be("same-access");
        expired.Should().Be(0);
        session.Clear();
    }

    private static SessionManager AuthenticatedSession(
        string accessToken = "not-a-jwt",
        string refreshTokenId = "refresh-id",
        string refreshToken = "refresh-token")
    {
        var session = new SessionManager();
        session.SetFromLogin(Login(accessToken, refreshTokenId, refreshToken));
        return session;
    }

    private static LoginResponse Login(string accessToken, string refreshTokenId, string refreshToken) => new()
    {
        AccessToken = accessToken,
        RefreshTokenId = refreshTokenId,
        RefreshToken = refreshToken,
        ExpiresIn = 900,
        IdleMinutes = 30,
    };

    private static ApiClient ClientReturning(HttpStatusCode status, SessionManager session)
        => ClientUsing(new ResponseHandler(status), session);

    private static ApiClient ClientUsing(HttpMessageHandler handler, SessionManager session)
    {
        var http = new HttpClient(handler);
        var settings = new AppSettings { ServerUrl = "https://watashi.test" };
        return new ApiClient(http, new SingleClientFactory(http), session, settings);
    }

    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class DeferredResponseHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpStatusCode> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpRequestMessage> RequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(HttpStatusCode status) => _response.TrySetResult(status);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestReceived.TrySetResult(request);
            var status = await _response.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
