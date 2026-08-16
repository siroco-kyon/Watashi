using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Watashi.Server.Data;
using Watashi.Server.Endpoints;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;

namespace Watashi.Tests;

public sealed class TransferV2EndpointContractTests
{
    [Fact]
    public void Public_routes_keep_the_published_upload_and_download_contract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<UploadSessionService>(_ => null!);
        builder.Services.AddScoped<AuditLogService>(_ => null!);
        builder.Services.AddScoped<AppDbContext>(_ => null!);
        builder.Services.AddScoped<NodeRouter>(_ => null!);
        builder.Services.AddScoped<EncryptionService>(_ => null!);
        builder.Services.AddScoped<PermissionService>(_ => null!);
        var app = builder.Build();
        app.MapTransferV2Endpoints();
        var patterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        patterns.Should().Contain(new[]
        {
            "/api/files/v2/uploads",
            "/api/files/v2/uploads/{sessionId:guid}",
            "/api/files/v2/uploads/{sessionId:guid}/chunks",
            "/api/files/v2/uploads/{sessionId:guid}/complete",
            "/api/files/v2/downloads/metadata",
            "/api/files/v2/downloads/range",
        });
    }

    [Fact]
    public async Task Chunk_reader_accepts_exact_body_and_rejects_declared_oversize()
    {
        var ok = new DefaultHttpContext();
        ok.Request.Body = new MemoryStream(new byte[] { 1, 2, 3 });
        ok.Request.ContentLength = 3;
        (await TransferV2Endpoints.ReadChunkBodyAsync(ok.Request, CancellationToken.None))
            .Should().Equal(1, 2, 3);

        var oversized = new DefaultHttpContext();
        oversized.Request.Body = Stream.Null;
        oversized.Request.ContentLength = TransferV2Limits.MaxChunkBytes + 1L;
        var act = async () => await TransferV2Endpoints.ReadChunkBodyAsync(
            oversized.Request, CancellationToken.None);

        await act.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.StatusCode == StatusCodes.Status413PayloadTooLarge &&
                        x.Code == "chunk_too_large");
    }

    [Fact]
    public async Task Chunk_reader_rejects_empty_and_honors_cancellation()
    {
        var empty = new DefaultHttpContext();
        empty.Request.Body = Stream.Null;
        empty.Request.ContentLength = 0;
        var emptyAct = async () => await TransferV2Endpoints.ReadChunkBodyAsync(
            empty.Request, CancellationToken.None);
        await emptyAct.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.Code == "empty_chunk");

        var cancelled = new DefaultHttpContext();
        cancelled.Request.Body = new MemoryStream(new byte[] { 1 });
        cancelled.Request.ContentLength = 1;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelAct = async () => await TransferV2Endpoints.ReadChunkBodyAsync(
            cancelled.Request, cts.Token);
        await cancelAct.Should().ThrowAsync<OperationCanceledException>();
    }
}
