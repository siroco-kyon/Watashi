using System.Net;
using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.DTOs.Auth;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Tests;

public sealed class ApiClientTransferV2Tests
{
    [Fact]
    public async Task Download_range_verifies_checksum_before_writing_output()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var session = AuthenticatedSession();
        try
        {
            var api = ClientWithResponse(data, TransferHashing.ComputeSha256Hex(data), session);
            using var output = new MemoryStream();

            await api.DownloadRangeV2Async(
                1, 2, "/a.bin", 0, data.Length, "\"etag\"", output,
                CancellationToken.None);

            output.ToArray().Should().Equal(data);
        }
        finally
        {
            session.Clear();
        }
    }

    [Fact]
    public async Task Download_range_checksum_mismatch_leaves_output_untouched()
    {
        var data = new byte[] { 9, 8, 7 };
        var session = AuthenticatedSession();
        try
        {
            var api = ClientWithResponse(data, new string('0', 64), session);
            using var output = new MemoryStream();
            output.WriteByte(42);
            output.Position = output.Length;

            var act = async () => await api.DownloadRangeV2Async(
                1, 2, "/a.bin", 0, data.Length, "\"etag\"", output,
                CancellationToken.None);

            await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*SHA-256*");
            output.ToArray().Should().Equal(new byte[] { 42 });
        }
        finally
        {
            session.Clear();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Download_range_rejects_mismatched_content_range_or_etag_before_writing(
        bool badRange,
        bool badEtag)
    {
        var data = new byte[] { 1, 2, 3 };
        var session = AuthenticatedSession();
        try
        {
            var handler = new RangeHandler(data, TransferHashing.ComputeSha256Hex(data), badRange, badEtag);
            var http = new HttpClient(handler);
            var api = new ApiClient(
                http, new SingleClientFactory(http), session,
                new AppSettings { ServerUrl = "https://watashi.test" });
            using var output = new MemoryStream();

            var act = () => api.DownloadRangeV2Async(
                1, 2, "/a.bin", 0, data.Length, "\"etag\"", output);

            await act.Should().ThrowAsync<InvalidDataException>();
            output.Length.Should().Be(0);
        }
        finally { session.Clear(); }
    }

    private static ApiClient ClientWithResponse(byte[] data, string checksum, SessionManager session)
    {
        var handler = new RangeHandler(data, checksum);
        var http = new HttpClient(handler);
        var settings = new AppSettings { ServerUrl = "https://watashi.test" };
        return new ApiClient(http, new SingleClientFactory(http), session, settings);
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

    private sealed class RangeHandler(
        byte[] data,
        string checksum,
        bool badRange = false,
        bool badEtag = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.IfMatch.Should().ContainSingle().Which.Tag.Should().Be("\"etag\"");
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data),
            };
            response.Headers.Add(TransferV2Headers.ChunkSha256, checksum);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(
                badEtag ? "\"different\"" : "\"etag\"");
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                badRange ? 1 : 0,
                data.Length - 1,
                data.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
