using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public class NodeRouterTransferV2Tests
{
    private static readonly CifsConnectionInfo Info = new("files.test", 445, "user", "pass", "share");

    [Fact]
    public async Task Direct_node_uses_all_local_v2_primitives()
    {
        var direct = new FakeCifsService();
        var forwarder = new FakeAgentForwarder();
        var router = new NodeRouter(direct, forwarder);
        var node = Node(NodeTypes.Direct);

        (await router.GetTransferMetadataAsync(node, Info, "/a.bin", CancellationToken.None))
            .Size.Should().Be(4);
        (await router.EnsureTempFileAsync(node, Info, "/.watashi-upload-a.tmp", CancellationToken.None))
            .Size.Should().Be(0);
        var range = await router.ReadRangeChunkAsync(
            node, Info, "/a.bin", 1, 2, CancellationToken.None);
        range.Data.Should().HaveCount(2);
        range.Sha256.Should().Be(TransferHashing.ComputeSha256Hex(range.Data));
        (await router.WriteTempChunkAsync(node, Info, "/.watashi-upload-a.tmp", 0,
            new byte[] { 1, 2 }, CancellationToken.None)).NextOffset.Should().Be(2);
        (await router.ComputeSha256Async(node, Info, "/a.bin", CancellationToken.None))
            .Size.Should().Be(4);
        await router.CommitTempAsync(node, Info, "/.watashi-upload-a.tmp", "/a.bin",
            replaceIfExists: true, CancellationToken.None);

        direct.Calls.Should().Equal("metadata", "ensure", "read", "write", "sha256", "commit");
        forwarder.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Agent_node_uses_all_forwarded_v2_primitives()
    {
        var direct = new FakeCifsService();
        var forwarder = new FakeAgentForwarder();
        var router = new NodeRouter(direct, forwarder);
        var node = Node(NodeTypes.Agent);

        await ExerciseAllAsync(router, node);

        direct.Calls.Should().BeEmpty();
        forwarder.Calls.Should().Equal("metadata", "ensure", "read", "write", "sha256", "commit");
        forwarder.LastNode.Should().BeSameAs(node);
    }

    [Fact]
    public async Task Gateway_route_uses_gateway_reachability_but_forwards_target_node()
    {
        var direct = new FakeCifsService();
        var forwarder = new FakeAgentForwarder();
        var router = new NodeRouter(direct, forwarder);
        var gateway = Node(NodeTypes.Agent, id: 1);
        var target = Node(NodeTypes.Agent, id: 2);
        target.HealthStatus = HealthStatuses.Unhealthy;
        target.GatewayNodeId = gateway.Id;
        target.GatewayNode = gateway;

        await ExerciseAllAsync(router, target);

        direct.Calls.Should().BeEmpty();
        forwarder.Calls.Should().HaveCount(6);
        forwarder.LastNode.Should().BeSameAs(target);
    }

    [Fact]
    public async Task V2_route_honors_pre_cancelled_token()
    {
        var router = new NodeRouter(new FakeCifsService(), new FakeAgentForwarder());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await router.GetTransferMetadataAsync(
            Node(NodeTypes.Direct), Info, "/a.bin", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task ExerciseAllAsync(NodeRouter router, ExecutionNode node)
    {
        await router.GetTransferMetadataAsync(node, Info, "/a.bin", CancellationToken.None);
        await router.EnsureTempFileAsync(node, Info, "/.watashi-upload-a.tmp", CancellationToken.None);
        await router.ReadRangeChunkAsync(
            node, Info, "/a.bin", 0, 2, CancellationToken.None);
        await router.WriteTempChunkAsync(node, Info, "/.watashi-upload-a.tmp", 0,
            new byte[] { 1, 2 }, CancellationToken.None);
        await router.ComputeSha256Async(node, Info, "/a.bin", CancellationToken.None);
        await router.CommitTempAsync(node, Info, "/.watashi-upload-a.tmp", "/a.bin",
            replaceIfExists: true, CancellationToken.None);
    }

    private static ExecutionNode Node(string type, int id = 1) => new()
    {
        Id = id,
        Name = $"node-{id}",
        NodeType = type,
        Endpoint = $"https://node-{id}.test",
        IsActive = true,
        HealthStatus = HealthStatuses.Healthy,
    };

    private sealed class FakeCifsService : CifsService
    {
        public List<string> Calls { get; } = new();

        public FakeCifsService() : base(new CifsSessionPool()) { }

        public override TransferFileMetadata GetTransferMetadata(
            CifsConnectionInfo info, string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("metadata");
            return new TransferFileMetadata(true, TransferFileTypes.File, 4, DateTime.UtcNow, false);
        }

        public override TransferFileMetadata EnsureTempFile(
            CifsConnectionInfo info, string tempPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("ensure");
            return new TransferFileMetadata(true, TransferFileTypes.File, 0, DateTime.UtcNow, false);
        }

        public override Stream OpenReadRange(
            CifsConnectionInfo info, string path, long offset, int length, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("read");
            return new MemoryStream(new byte[Math.Min(2, length)]);
        }

        public override TransferChunkWriteResult WriteTempChunk(
            CifsConnectionInfo info, string tempPath, long offset, ReadOnlyMemory<byte> chunk,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("write");
            return new TransferChunkWriteResult(offset, chunk.Length, chunk.Length,
                offset + chunk.Length, false);
        }

        public override TransferSha256Result ComputeSha256(
            CifsConnectionInfo info, string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("sha256");
            return new TransferSha256Result("SHA-256", new string('0', 64), 4);
        }

        public override void CommitTemp(
            CifsConnectionInfo info, string tempPath, string targetPath,
            bool replaceIfExists, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("commit");
        }
    }

    private sealed class FakeAgentForwarder : AgentForwarder
    {
        public List<string> Calls { get; } = new();
        public ExecutionNode? LastNode { get; private set; }

        public FakeAgentForwarder()
            : base(new EmptyHttpClientFactory(), new ConfigurationBuilder().Build(),
                NullLogger<AgentForwarder>.Instance)
        { }

        public override Task<TransferFileMetadata> GetTransferMetadataAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            Record("metadata", node, ct);
            return Task.FromResult(new TransferFileMetadata(
                true, TransferFileTypes.File, 4, DateTime.UtcNow, false));
        }

        public override Task<TransferFileMetadata> EnsureTempFileAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, CancellationToken ct)
        {
            Record("ensure", node, ct);
            return Task.FromResult(new TransferFileMetadata(
                true, TransferFileTypes.File, 0, DateTime.UtcNow, false));
        }

        public override Task<Stream> OpenReadRangeAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, long offset, int length,
            CancellationToken ct)
        {
            Record("read", node, ct);
            return Task.FromResult<Stream>(new MemoryStream(new byte[Math.Min(2, length)]));
        }

        public override Task<TransferReadChunk> ReadRangeChunkAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, long offset, int length,
            CancellationToken ct)
        {
            Record("read", node, ct);
            var data = new byte[Math.Min(2, length)];
            return Task.FromResult(new TransferReadChunk(
                data, TransferHashing.ComputeSha256Hex(data)));
        }

        public override Task<TransferChunkWriteResult> WriteTempChunkAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, long offset,
            ReadOnlyMemory<byte> chunk, CancellationToken ct)
        {
            Record("write", node, ct);
            return Task.FromResult(new TransferChunkWriteResult(
                offset, chunk.Length, chunk.Length, offset + chunk.Length, false));
        }

        public override Task<TransferSha256Result> ComputeSha256Async(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            Record("sha256", node, ct);
            return Task.FromResult(new TransferSha256Result("SHA-256", new string('0', 64), 4));
        }

        public override Task CommitTempAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, string targetPath,
            bool replaceIfExists, CancellationToken ct)
        {
            Record("commit", node, ct);
            return Task.CompletedTask;
        }

        private void Record(string call, ExecutionNode node, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(call);
            LastNode = node;
        }
    }

    private sealed class EmptyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
