using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class TransferV2Endpoints
{
    public static IEndpointRouteBuilder MapTransferV2Endpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files/v2").RequireAuthorization();

        group.MapPost("/uploads", async (
            CreateUploadSessionRequest request,
            UploadSessionService sessions,
            AuditLogService audit,
            ClaimsPrincipal principal,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await sessions.CreateAsync(userId, request, ct);
                if (result.Changed)
                {
                    await audit.LogAsync(principal, ctx, Operations.Upload,
                        request.HostId, request.ShareId, result.Session.Path,
                        AuditResults.Success, bytesTransferred: 0,
                        durationMs: sw.ElapsedMilliseconds,
                        executionNodeId: result.ExecutionNodeId,
                        usedPermissionId: result.PermissionId,
                        ct: CancellationToken.None);
                    return Results.Created($"/api/files/v2/uploads/{result.Session.SessionId}", result.Session);
                }
                return Results.Ok(result.Session);
            }
            catch (Exception ex)
            {
                await AuditFailureAsync(audit, principal, ctx, request.HostId, request.ShareId,
                    request.Path, ex, sw.ElapsedMilliseconds);
                return MapError(ex);
            }
        });

        group.MapGet("/uploads/{sessionId:guid}", async (
            Guid sessionId,
            UploadSessionService sessions,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            try
            {
                var result = await sessions.GetStatusAsync(userId, sessionId, ct);
                return Results.Ok(result.Session);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapPut("/uploads/{sessionId:guid}/chunks", async (
            Guid sessionId,
            long offset,
            UploadSessionService sessions,
            ClaimsPrincipal principal,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            if (!ctx.Request.Headers.TryGetValue(TransferV2Headers.ChunkSha256, out var checksum) ||
                string.IsNullOrWhiteSpace(checksum))
                return ErrorResult(StatusCodes.Status400BadRequest, "chunk_checksum_required",
                    $"{TransferV2Headers.ChunkSha256} ヘッダが必要です。");
            try
            {
                var chunk = await ReadChunkBodyAsync(ctx.Request, ct);
                var result = await sessions.WriteChunkAsync(
                    userId, sessionId, offset, chunk, checksum.ToString(), ct);
                return Results.Ok(result.Session);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapPost("/uploads/{sessionId:guid}/complete", async (
            Guid sessionId,
            UploadSessionService sessions,
            AppDbContext db,
            AuditLogService audit,
            ClaimsPrincipal principal,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await sessions.CompleteAsync(userId, sessionId, ct);
                if (result.Changed)
                {
                    await audit.LogAsync(principal, ctx, Operations.Upload,
                        result.Session.HostId, result.Session.ShareId, result.Session.Path,
                        AuditResults.Success, bytesTransferred: result.Session.TotalSize,
                        durationMs: sw.ElapsedMilliseconds,
                        executionNodeId: result.ExecutionNodeId,
                        usedPermissionId: result.PermissionId,
                        ct: CancellationToken.None);
                }
                return Results.Ok(result.Session);
            }
            catch (Exception ex)
            {
                var scope = await FindAuditScopeAsync(db, userId, sessionId);
                await AuditFailureAsync(audit, principal, ctx, scope.HostId, scope.ShareId,
                    scope.Path ?? $"session:{sessionId}", ex, sw.ElapsedMilliseconds);
                return MapError(ex);
            }
        });

        group.MapDelete("/uploads/{sessionId:guid}", async (
            Guid sessionId,
            UploadSessionService sessions,
            AppDbContext db,
            AuditLogService audit,
            ClaimsPrincipal principal,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await sessions.CancelAsync(userId, sessionId, ct);
                if (result.Changed && result.Session.Status == UploadSessionStatuses.Cancelled)
                {
                    await audit.LogAsync(principal, ctx, Operations.Upload,
                        result.Session.HostId, result.Session.ShareId, result.Session.Path,
                        AuditResults.Warning, "cancelled",
                        bytesTransferred: result.Session.UploadedOffset,
                        durationMs: sw.ElapsedMilliseconds,
                        executionNodeId: result.ExecutionNodeId,
                        ct: CancellationToken.None);
                }
                return Results.Ok(result.Session);
            }
            catch (Exception ex)
            {
                var scope = await FindAuditScopeAsync(db, userId, sessionId);
                await AuditFailureAsync(audit, principal, ctx, scope.HostId, scope.ShareId,
                    scope.Path ?? $"session:{sessionId}", ex, sw.ElapsedMilliseconds);
                return MapError(ex);
            }
        });

        group.MapGet("/downloads/metadata", async (
            int hostId,
            int shareId,
            string path,
            HttpContext ctx,
            AppDbContext db,
            NodeRouter router,
            EncryptionService encryption,
            PermissionService permissions,
            AuditLogService audit,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            return await FileEndpoints.ExecuteAsync(audit, principal, ctx,
                Operations.Read, hostId, shareId, path,
                db, encryption, permissions, ct, async (auth, execution) =>
                {
                    var metadata = await router.GetTransferMetadataAsync(
                        execution.Node, execution.Info, auth.NormalizedPath, ct);
                    string? sha256 = null;
                    string? etag = null;
                    if (metadata.Exists && metadata.Type == TransferFileTypes.File && !metadata.IsReparsePoint)
                    {
                        var identity = await router.ComputeSha256Async(
                            execution.Node, execution.Info, auth.NormalizedPath, ct);
                        var afterHash = await router.GetTransferMetadataAsync(
                            execution.Node, execution.Info, auth.NormalizedPath, ct);
                        if (!SameFileGeneration(metadata, afterHash) || identity.Size != afterHash.Size)
                            return new FileEndpoints.FailureResult(
                                ErrorResult(StatusCodes.Status409Conflict, "source_changed",
                                    "metadata取得中にファイルが変更されました。再試行してください。"),
                                "source_changed");
                        sha256 = TransferV2Validation.NormalizeSha256(identity.Hash);
                        metadata = afterHash;
                        etag = TransferV2Validation.BuildMetadataETag(metadata);
                    }
                    if (etag is not null)
                    {
                        ctx.Response.Headers.ETag = etag;
                        ctx.Response.Headers.AcceptRanges = "bytes";
                    }
                    return Results.Ok(new TransferDownloadMetadataDto
                    {
                        Exists = metadata.Exists,
                        Type = metadata.Type,
                        Size = metadata.Size,
                        ModifiedAtUtc = metadata.ModifiedAtUtc,
                        Sha256 = sha256,
                        ETag = etag,
                    });
                }, auditOperation: Operations.Download);
        });

        group.MapGet("/downloads/range", async (
            int hostId,
            int shareId,
            string path,
            long offset,
            int length,
            string? etag,
            HttpContext ctx,
            AppDbContext db,
            NodeRouter router,
            EncryptionService encryption,
            PermissionService permissions,
            AuditLogService audit,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            return await FileEndpoints.ExecuteAsync(audit, principal, ctx,
                Operations.Read, hostId, shareId, path,
                db, encryption, permissions, ct, async (auth, execution) =>
                {
                    try
                    {
                        TransferV2Validation.ValidateReadRange(offset, length);
                    }
                    catch (ArgumentException ex)
                    {
                        return new FileEndpoints.FailureResult(
                            ErrorResult(StatusCodes.Status400BadRequest, "invalid_range", ex.Message),
                            "invalid_range");
                    }

                    var metadata = await router.GetTransferMetadataAsync(
                        execution.Node, execution.Info, auth.NormalizedPath, ct);
                    if (!metadata.Exists)
                        return new FileEndpoints.FailureResult(
                            ErrorResult(StatusCodes.Status404NotFound, "file_not_found", "ファイルが見つかりません。"),
                            "file_not_found");
                    if (metadata.IsReparsePoint || metadata.Type != TransferFileTypes.File)
                        return new FileEndpoints.FailureResult(
                            ErrorResult(StatusCodes.Status409Conflict, "not_regular_file",
                                "通常ファイル以外はdownloadできません。"),
                            "not_regular_file");

                    var size = metadata.Size ?? 0;
                    if (offset >= size)
                        return new FileEndpoints.FailureResult(
                            ErrorResult(StatusCodes.Status416RangeNotSatisfiable, "range_not_satisfiable",
                                "offset はファイルサイズ未満で指定してください。", size, offset),
                            "range_not_satisfiable");

                    var currentETag = TransferV2Validation.BuildMetadataETag(metadata);
                    var requestedETag = string.IsNullOrWhiteSpace(etag)
                        ? ctx.Request.Headers.IfMatch.ToString()
                        : etag;
                    if (!string.IsNullOrWhiteSpace(requestedETag) &&
                        !ETagMatches(requestedETag, currentETag))
                        return new FileEndpoints.FailureResult(
                            ErrorResult(StatusCodes.Status412PreconditionFailed, "etag_mismatch",
                                "ファイルがmetadata取得後に変更されました。"),
                            "etag_mismatch");

                    var chunk = await router.ReadRangeChunkAsync(
                        execution.Node, execution.Info, auth.NormalizedPath, offset, length, ct);
                    var actualLength = chunk.Data.LongLength;
                    if (actualLength <= 0 || actualLength > length)
                        throw new InvalidDataException("range chunk の長さが不正です。");
                    var normalizedChunkHash = TransferV2Validation.NormalizeSha256(chunk.Sha256);
                    if (!string.Equals(
                            TransferHashing.ComputeSha256Hex(chunk.Data),
                            normalizedChunkHash,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("range chunk の SHA-256 が一致しません。");

                    ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                    ctx.Response.ContentType = "application/octet-stream";
                    ctx.Response.ContentLength = actualLength;
                    ctx.Response.Headers.ETag = currentETag;
                    ctx.Response.Headers.AcceptRanges = "bytes";
                    ctx.Response.Headers.ContentRange = $"bytes {offset}-{offset + actualLength - 1}/{size}";
                    ctx.Response.Headers[TransferV2Headers.ChunkSha256] = normalizedChunkHash;
                    var counting = new FileEndpoints.CountingStream(ctx.Response.Body);
                    await counting.WriteAsync(chunk.Data, ct);
                    ctx.Items["bytes"] = counting.BytesWritten;
                    return Results.Empty;
                }, auditOperation: Operations.Download);
        });

        return app;
    }

    internal static async Task<byte[]> ReadChunkBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is <= 0)
            throw new TransferSessionException(StatusCodes.Status400BadRequest, "empty_chunk",
                "chunk body は1バイト以上必要です。");
        if (request.ContentLength > TransferV2Limits.MaxChunkBytes)
            throw new TransferSessionException(StatusCodes.Status413PayloadTooLarge, "chunk_too_large",
                $"chunk は {TransferV2Limits.MaxChunkBytes} バイト以下にしてください。");

        var capacity = request.ContentLength.HasValue
            ? checked((int)request.ContentLength.Value)
            : 0;
        using var buffer = new MemoryStream(capacity);
        var rented = new byte[128 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = TransferV2Limits.MaxChunkBytes + 1L - buffer.Length;
            if (remaining <= 0)
                throw new TransferSessionException(StatusCodes.Status413PayloadTooLarge, "chunk_too_large",
                    $"chunk は {TransferV2Limits.MaxChunkBytes} バイト以下にしてください。");
            var read = await request.Body.ReadAsync(
                rented.AsMemory(0, (int)Math.Min(rented.Length, remaining)), ct);
            if (read == 0) break;
            buffer.Write(rented, 0, read);
        }
        if (buffer.Length == 0)
            throw new TransferSessionException(StatusCodes.Status400BadRequest, "empty_chunk",
                "chunk body は1バイト以上必要です。");
        if (request.ContentLength.HasValue && buffer.Length != request.ContentLength.Value)
            throw new TransferSessionException(StatusCodes.Status400BadRequest, "chunk_length_mismatch",
                "Content-Length と実際の chunk 長が一致しません。");
        return buffer.ToArray();
    }

    private static bool ETagMatches(string requested, string current)
        => requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(value => value == "*" || string.Equals(value, current, StringComparison.Ordinal));

    private static bool SameFileGeneration(
        TransferFileMetadata before,
        TransferFileMetadata after)
        => before.Exists && after.Exists &&
           before.Type == TransferFileTypes.File && after.Type == TransferFileTypes.File &&
           !before.IsReparsePoint && !after.IsReparsePoint &&
           before.Size == after.Size &&
           before.ModifiedAtUtc?.ToUniversalTime().Ticks ==
               after.ModifiedAtUtc?.ToUniversalTime().Ticks;

    private static IResult MapError(Exception ex) => ex switch
    {
        TransferSessionException session => ErrorResult(
            session.StatusCode, session.Code, session.Message,
            session.ExpectedOffset, session.ActualOffset),
        TransferOffsetMismatchException mismatch => ErrorResult(
            StatusCodes.Status409Conflict, "offset_mismatch", mismatch.Message,
            mismatch.ExpectedOffset, mismatch.ActualOffset),
        TransferRangeNotSatisfiableException range => ErrorResult(
            StatusCodes.Status416RangeNotSatisfiable, "range_not_satisfiable", range.Message,
            range.Size, range.Offset),
        TransferReparsePointException => ErrorResult(
            StatusCodes.Status409Conflict, "reparse_point_rejected", ex.Message),
        ArgumentException => ErrorResult(StatusCodes.Status400BadRequest, "invalid_request", ex.Message),
        OperationCanceledException => throw ex,
        _ => FileEndpoints.MapExecutionError(ex),
    };

    private static IResult ErrorResult(
        int status,
        string code,
        string message,
        long? expectedOffset = null,
        long? actualOffset = null)
        => Results.Json(new { error = message, code, expectedOffset, actualOffset }, statusCode: status);

    private static async Task AuditFailureAsync(
        AuditLogService audit,
        ClaimsPrincipal principal,
        HttpContext ctx,
        int? hostId,
        int? shareId,
        string path,
        Exception ex,
        long durationMs)
    {
        var reason = ex switch
        {
            TransferSessionException session => session.Code,
            OperationCanceledException => "cancelled",
            _ => "transfer_failed",
        };
        await audit.LogAsync(principal, ctx, Operations.Upload,
            hostId, shareId, PathHelper.NormalizePath(path), AuditResults.Failure, reason,
            durationMs: durationMs, ct: CancellationToken.None);
    }

    private static async Task<(int? HostId, int? ShareId, string? Path)> FindAuditScopeAsync(
        AppDbContext db,
        int userId,
        Guid sessionId)
    {
        var row = await db.UploadSessions.AsNoTracking()
            .Where(s => s.Id == sessionId && s.UserId == userId)
            .Select(s => new { s.HostId, s.ShareId, s.TargetPath })
            .FirstOrDefaultAsync(CancellationToken.None);
        return row is null ? (null, null, null) : (row.HostId, row.ShareId, row.TargetPath);
    }
}
