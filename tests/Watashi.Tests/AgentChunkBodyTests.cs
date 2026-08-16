using FluentAssertions;
using Microsoft.AspNetCore.Http;
using System.Text;
using Watashi.Agent.Endpoints;

namespace Watashi.Tests;

public class AgentChunkBodyTests
{
    [Fact]
    public async Task ReadChunkBody_reads_exact_declared_length()
    {
        var ctx = ContextWithBody(new byte[] { 1, 2, 3, 4 });

        var chunk = await AgentEndpoints.ReadChunkBodyAsync(ctx.Request, 4, CancellationToken.None);

        chunk.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task ReadChunkBody_rejects_content_length_mismatch()
    {
        var ctx = ContextWithBody(new byte[] { 1, 2, 3 });

        var act = async () => await AgentEndpoints.ReadChunkBodyAsync(
            ctx.Request, 4, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*Content-Length*");
    }

    [Fact]
    public async Task ReadChunkBody_rejects_truncated_or_oversized_stream_without_content_length()
    {
        var shortRequest = ContextWithBody(new byte[] { 1, 2, 3 }, includeLength: false).Request;
        var longRequest = ContextWithBody(new byte[] { 1, 2, 3, 4, 5 }, includeLength: false).Request;

        var shortAct = async () => await AgentEndpoints.ReadChunkBodyAsync(
            shortRequest, 4, CancellationToken.None);
        var longAct = async () => await AgentEndpoints.ReadChunkBodyAsync(
            longRequest, 4, CancellationToken.None);

        await shortAct.Should().ThrowAsync<InvalidDataException>().WithMessage("*途中で終了*");
        await longAct.Should().ThrowAsync<InvalidDataException>().WithMessage("*宣言長*");
    }

    [Fact]
    public async Task ReadChunkBody_honors_cancellation()
    {
        var ctx = ContextWithBody(new byte[] { 1, 2, 3, 4 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await AgentEndpoints.ReadChunkBodyAsync(ctx.Request, 4, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Chunk_header_decodes_forwarder_wire_contract()
    {
        var ctx = new DefaultHttpContext();
        var json = """
            {"host":"files.test","port":445,"share":"s","credUser":"u","credPass":"p","path":"/.watashi-upload-a.tmp","offset":12,"length":3}
            """;
        ctx.Request.Headers[AgentChunkHeader.HeaderName] =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var header = AgentChunkHeader.Extract(ctx);

        header.Should().NotBeNull();
        header!.Host.Should().Be("files.test");
        header.Path.Should().Be("/.watashi-upload-a.tmp");
        header.Offset.Should().Be(12);
        header.Length.Should().Be(3);
    }

    [Fact]
    public async Task Range_buffering_requires_exact_declared_length()
    {
        var exact = await AgentEndpoints.ReadRangeStreamAsync(
            new MemoryStream(new byte[] { 1, 2, 3 }), 3, CancellationToken.None);
        exact.Should().Equal(1, 2, 3);

        var shortAct = async () => await AgentEndpoints.ReadRangeStreamAsync(
            new MemoryStream(new byte[] { 1, 2 }), 3, CancellationToken.None);
        var longAct = async () => await AgentEndpoints.ReadRangeStreamAsync(
            new MemoryStream(new byte[] { 1, 2, 3, 4 }), 3, CancellationToken.None);
        await shortAct.Should().ThrowAsync<EndOfStreamException>();
        await longAct.Should().ThrowAsync<InvalidDataException>();
    }

    private static DefaultHttpContext ContextWithBody(byte[] body, bool includeLength = true)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(body);
        if (includeLength) ctx.Request.ContentLength = body.Length;
        return ctx;
    }
}
