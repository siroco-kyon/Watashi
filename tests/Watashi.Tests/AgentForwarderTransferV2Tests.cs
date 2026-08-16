using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public class AgentForwarderTransferV2Tests
{
    private static readonly CifsConnectionInfo Info =
        new("files.internal", 445, "svc-user", "secret-pass", "reports");

    [Fact]
    public async Task Gateway_forwards_all_v2_operations_to_entry_agent_with_target_header()
    {
        var handler = new CapturingHandler(ResponseFor);
        var forwarder = Forwarder(handler);
        var gateway = AgentNode(1, "https://gateway.test:8443");
        var target = AgentNode(2, "https://leaf.test:9443");
        target.GatewayNodeId = gateway.Id;
        target.GatewayNode = gateway;

        var metadata = await forwarder.GetTransferMetadataAsync(
            target, Info, "/a.bin", CancellationToken.None);
        var temp = await forwarder.EnsureTempFileAsync(
            target, Info, "/.watashi-upload-session_1.tmp", CancellationToken.None);
        var range = await forwarder.ReadRangeChunkAsync(
            target, Info, "/a.bin", 5, 4, CancellationToken.None);
        var write = await forwarder.WriteTempChunkAsync(
            target, Info, "/.watashi-upload-session_1.tmp", 8,
            new byte[] { 9, 8, 7 }, CancellationToken.None);
        var hash = await forwarder.ComputeSha256Async(
            target, Info, "/a.bin", CancellationToken.None);
        await forwarder.CommitTempAsync(
            target, Info, "/.watashi-upload-session_1.tmp", "/a.bin",
            replaceIfExists: true, CancellationToken.None);

        metadata.Size.Should().Be(12);
        temp.Size.Should().Be(0);
        range.Data.Should().Equal(1, 2, 3);
        range.Sha256.Should().Be(TransferHashing.ComputeSha256Hex(range.Data));
        write.NextOffset.Should().Be(11);
        hash.Size.Should().Be(12);
        handler.Requests.Select(x => x.Path).Should().Equal(
            "/agent/v2/files/metadata",
            "/agent/v2/files/ensure-temp",
            "/agent/v2/files/read",
            "/agent/v2/files/write-chunk",
            "/agent/v2/files/sha256",
            "/agent/v2/files/commit-temp");
        handler.Requests.Should().OnlyContain(x => x.Host == "gateway.test");
        handler.Requests.Should().OnlyContain(x =>
            x.Header(AgentForwarder.ForwardToHeader) == target.Endpoint);
        handler.Requests.Should().OnlyContain(x => x.Header("X-Watashi-Secret") == "routing-secret");

        var chunkRequest = handler.Requests[3];
        chunkRequest.Body.Should().Equal(9, 8, 7);
        using var headerJson = JsonDocument.Parse(Convert.FromBase64String(
            chunkRequest.Header("X-Watashi-Cifs-V2")!));
        headerJson.RootElement.GetProperty("path").GetString()
            .Should().Be("/.watashi-upload-session_1.tmp");
        headerJson.RootElement.GetProperty("offset").GetInt64().Should().Be(8);
        headerJson.RootElement.GetProperty("length").GetInt32().Should().Be(3);

        using var commitJson = JsonDocument.Parse(handler.Requests[5].Body);
        commitJson.RootElement.GetProperty("tempPath").GetString()
            .Should().Be("/.watashi-upload-session_1.tmp");
        commitJson.RootElement.GetProperty("targetPath").GetString().Should().Be("/a.bin");
        commitJson.RootElement.GetProperty("replaceIfExists").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Direct_agent_request_uses_agent_endpoint_without_gateway_header()
    {
        var handler = new CapturingHandler(ResponseFor);
        var node = AgentNode(1, "https://agent.test:8443");
        var forwarder = Forwarder(handler);

        await forwarder.GetTransferMetadataAsync(node, Info, "/a.bin", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Host.Should().Be("agent.test");
        handler.Requests[0].Header(AgentForwarder.ForwardToHeader).Should().BeNull();
    }

    [Fact]
    public async Task Range_response_larger_than_requested_is_rejected()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[5]),
        });
        var forwarder = Forwarder(handler);

        var act = async () => await forwarder.OpenReadRangeAsync(
            AgentNode(1, "https://agent.test"), Info, "/a.bin", 0, 4, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*Content-Length*");
    }

    [Fact]
    public async Task Range_stream_detects_body_truncated_before_content_length()
    {
        var handler = new CapturingHandler(_ =>
        {
            var content = new StreamContent(new MemoryStream(new byte[] { 1, 2 }));
            content.Headers.ContentLength = 3;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var forwarder = Forwarder(handler);
        await using var stream = await forwarder.OpenReadRangeAsync(
            AgentNode(1, "https://agent.test"), Info, "/a.bin", 0, 4, CancellationToken.None);
        using var output = new MemoryStream();

        var act = async () => await stream.CopyToAsync(output);

        await act.Should().ThrowAsync<EndOfStreamException>().WithMessage("*途中で終了*");
    }

    [Fact]
    public async Task Chunk_offset_conflict_is_preserved_as_agent_relay_409()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new
            {
                error = "offset mismatch",
                expectedOffset = 8,
                actualOffset = 4,
            }),
        });
        var forwarder = Forwarder(handler);

        var act = async () => await forwarder.WriteTempChunkAsync(
            AgentNode(1, "https://agent.test"), Info, "/.watashi-upload-a.tmp", 8,
            new byte[] { 1 }, CancellationToken.None);

        await act.Should().ThrowAsync<AgentRelayException>()
            .Where(x => x.StatusCode == StatusCodes.Status409Conflict && x.Message == "offset mismatch");
    }

    [Fact]
    public async Task Range_chunk_rejects_checksum_changed_in_agent_http_path()
    {
        var handler = new CapturingHandler(_ => RangeResponse(
            new byte[] { 1, 2, 3 }, new string('0', 64)));
        var forwarder = Forwarder(handler);

        var act = async () => await forwarder.ReadRangeChunkAsync(
            AgentNode(1, "https://agent.test"), Info, "/a.bin", 0, 3,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*SHA-256*");
    }

    private static AgentForwarder Forwarder(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Routing:SharedSecret"] = "routing-secret",
            ["Http:FileTransferTimeoutMinutes"] = "2",
        }).Build();
        return new AgentForwarder(
            new SingleHandlerFactory(handler),
            config,
            NullLogger<AgentForwarder>.Instance);
    }

    private static ExecutionNode AgentNode(int id, string endpoint) => new()
    {
        Id = id,
        Name = $"agent-{id}",
        NodeType = NodeTypes.Agent,
        Endpoint = endpoint,
        IsActive = true,
        HealthStatus = HealthStatuses.Healthy,
    };

    private static HttpResponseMessage ResponseFor(RequestSnapshot request)
        => request.Path switch
        {
            "/agent/v2/files/metadata" => Json(new TransferFileMetadata(
                true, TransferFileTypes.File, 12, DateTime.UtcNow, false)),
            "/agent/v2/files/ensure-temp" => Json(new TransferFileMetadata(
                true, TransferFileTypes.File, 0, DateTime.UtcNow, false)),
            "/agent/v2/files/read" => RangeResponse(new byte[] { 1, 2, 3 }),
            "/agent/v2/files/write-chunk" => Json(new TransferChunkWriteResult(
                8, 3, 3, 11, false)),
            "/agent/v2/files/sha256" => Json(new TransferSha256Result(
                "SHA-256", new string('a', 64), 12)),
            "/agent/v2/files/commit-temp" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private static HttpResponseMessage RangeResponse(byte[] data, string? checksum = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data),
        };
        response.Headers.Add(
            Watashi.Shared.DTOs.Files.TransferV2Headers.ChunkSha256,
            checksum ?? TransferHashing.ComputeSha256Hex(data));
        return response;
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler(
        Func<RequestSnapshot, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers
                .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            var snapshot = new RequestSnapshot(
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Host,
                headers,
                request.Content is null
                    ? Array.Empty<byte>()
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Requests.Add(snapshot);
            return responder(snapshot);
        }
    }

    private sealed record RequestSnapshot(
        string Path,
        string Host,
        IReadOnlyDictionary<string, string[]> Headers,
        byte[] Body)
    {
        public string? Header(string name)
            => Headers.TryGetValue(name, out var values) ? values.SingleOrDefault() : null;
    }
}
