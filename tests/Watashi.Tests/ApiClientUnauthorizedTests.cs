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

    private static SessionManager AuthenticatedSession()
    {
        var session = new SessionManager();
        session.SetFromLogin(new LoginResponse
        {
            AccessToken = "not-a-jwt",
            RefreshTokenId = "refresh-id",
            RefreshToken = "refresh-token",
            ExpiresIn = 900,
            IdleMinutes = 30,
        });
        return session;
    }

    private static ApiClient ClientReturning(HttpStatusCode status, SessionManager session)
    {
        var http = new HttpClient(new ResponseHandler(status));
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

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
