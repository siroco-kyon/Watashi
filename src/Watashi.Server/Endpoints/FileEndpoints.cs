using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Server.Services.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class FileEndpoints
{
    private const int PageSize = 200;

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files").RequireAuthorization();

        group.MapGet("/", async (
            int hostId, int shareId, string? path, int? page, string? sort,
            HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var auth = await ResolveAuthAsync(principal, hostId, shareId, path ?? "/", Operations.Read, db, perms, ct);
            if (auth.Failure is not null)
            {
                await audit.LogAsync(principal, ctx, Operations.Read, hostId, shareId, path, AuditResults.Failure, "denied", durationMs: sw.ElapsedMilliseconds, ct: ct);
                return auth.Failure;
            }
            var execCtx = await BuildExecutionContextAsync(db, enc, hostId, shareId, ct);
            if (execCtx is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });

            try
            {
                var entries = (await router.ListAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct)).ToList();
                entries = (sort ?? "name") switch
                {
                    "name_desc" => entries.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                    "date" => entries.OrderBy(e => e.ModifiedAt).ToList(),
                    "date_desc" => entries.OrderByDescending(e => e.ModifiedAt).ToList(),
                    "size" => entries.OrderBy(e => e.Size ?? -1).ToList(),
                    "size_desc" => entries.OrderByDescending(e => e.Size ?? -1).ToList(),
                    _ => entries.OrderBy(e => e.Type == "directory" ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                };
                int p = Math.Max(1, page ?? 1);
                int total = entries.Count;
                var paged = entries.Skip((p - 1) * PageSize).Take(PageSize).ToList();
                bool isRoot = await perms.IsPermissionRootAsync(auth.UserId, shareId, auth.NormalizedPath, ct);
                var parent = new FileEntry { Name = "..", Type = "parent", CanGoUp = !isRoot && auth.NormalizedPath != "/" };
                var response = new FileListResponse
                {
                    CurrentPath = auth.NormalizedPath,
                    Entries = new List<FileEntry>(paged.Count + 1) { parent }.Concat(paged).ToList(),
                    Page = p, TotalCount = total,
                };
                await audit.LogAsync(principal, ctx, Operations.Read, hostId, shareId, auth.NormalizedPath, AuditResults.Success, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct);
                return Results.Ok(response);
            }
            catch (NodeUnreachableException nue)
            {
                await audit.LogAsync(principal, ctx, Operations.Read, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, nue.Message, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                await audit.LogAsync(principal, ctx, Operations.Read, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, ex.Message, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct);
                throw;
            }
        });

        group.MapGet("/download", async (
            int hostId, int shareId, string path,
            HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var auth = await ResolveAuthAsync(principal, hostId, shareId, path, Operations.Read, db, perms, ct);
            if (auth.Failure is not null) { await audit.LogAsync(principal, ctx, Operations.Read, hostId, shareId, path, AuditResults.Failure, "denied", durationMs: sw.ElapsedMilliseconds, ct: ct); return auth.Failure; }
            var execCtx = await BuildExecutionContextAsync(db, enc, hostId, shareId, ct);
            if (execCtx is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });

            var fileName = Path.GetFileName(auth.NormalizedPath);
            ctx.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
            ctx.Response.ContentType = "application/octet-stream";

            long total = 0;
            try
            {
                await using var stream = await router.OpenReadAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct);
                var counting = new CountingStream(ctx.Response.Body);
                await stream.CopyToAsync(counting, 4 * 1024 * 1024, ct);
                total = counting.BytesWritten;
            }
            catch (NodeUnreachableException nue) { await audit.LogAsync(principal, ctx, Operations.Read, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, nue.Message, durationMs: sw.ElapsedMilliseconds, bytesTransferred: total, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (Exception ex) { await audit.LogAsync(principal, ctx, Operations.Read, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, ex.Message, durationMs: sw.ElapsedMilliseconds, bytesTransferred: total, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); throw; }
            await audit.LogAsync(principal, ctx, Operations.Read, hostId, shareId, auth.NormalizedPath, AuditResults.Success, durationMs: sw.ElapsedMilliseconds, bytesTransferred: total, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct);
            return Results.Empty;
        });

        group.MapPost("/upload", async (
            int hostId, int shareId, string path,
            HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var auth = await ResolveAuthAsync(principal, hostId, shareId, path, Operations.Write, db, perms, ct);
            if (auth.Failure is not null) { await audit.LogAsync(principal, ctx, Operations.Write, hostId, shareId, path, AuditResults.Failure, "denied", durationMs: sw.ElapsedMilliseconds, ct: ct); return auth.Failure; }
            var execCtx = await BuildExecutionContextAsync(db, enc, hostId, shareId, ct);
            if (execCtx is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });

            var counting = new CountingStream(ctx.Request.Body, readSide: true);
            try
            {
                await router.UploadAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, counting, ct);
            }
            catch (NodeUnreachableException nue) { await audit.LogAsync(principal, ctx, Operations.Write, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, nue.Message, durationMs: sw.ElapsedMilliseconds, bytesTransferred: counting.BytesWritten, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (Exception ex) { await audit.LogAsync(principal, ctx, Operations.Write, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, ex.Message, durationMs: sw.ElapsedMilliseconds, bytesTransferred: counting.BytesWritten, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); throw; }
            await audit.LogAsync(principal, ctx, Operations.Write, hostId, shareId, auth.NormalizedPath, AuditResults.Success, durationMs: sw.ElapsedMilliseconds, bytesTransferred: counting.BytesWritten, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct);
            return Results.NoContent();
        });

        group.MapDelete("/", async (
            int hostId, int shareId, string path,
            HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var auth = await ResolveAuthAsync(principal, hostId, shareId, path, Operations.Delete, db, perms, ct);
            if (auth.Failure is not null) { await audit.LogAsync(principal, ctx, Operations.Delete, hostId, shareId, path, AuditResults.Failure, "denied", durationMs: sw.ElapsedMilliseconds, ct: ct); return auth.Failure; }
            if (await perms.IsPermissionRootAsync(auth.UserId, shareId, auth.NormalizedPath, ct))
            {
                await audit.LogAsync(principal, ctx, Operations.Delete, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, "permission_root", durationMs: sw.ElapsedMilliseconds, usedPermissionId: auth.PermissionId, ct: ct);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var execCtx = await BuildExecutionContextAsync(db, enc, hostId, shareId, ct);
            if (execCtx is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });
            try { await router.DeleteAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct); }
            catch (NodeUnreachableException nue) { await audit.LogAsync(principal, ctx, Operations.Delete, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, nue.Message, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (Exception ex) { await audit.LogAsync(principal, ctx, Operations.Delete, hostId, shareId, auth.NormalizedPath, AuditResults.Failure, ex.Message, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); throw; }
            await audit.LogAsync(principal, ctx, Operations.Delete, hostId, shareId, auth.NormalizedPath, AuditResults.Success, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct);
            return Results.NoContent();
        });

        group.MapPost("/rename", async (
            RenameRequest req, HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var auth = await ResolveAuthAsync(principal, req.HostId, req.ShareId, req.OldPath, Operations.Rename, db, perms, ct);
            if (auth.Failure is not null) { await audit.LogAsync(principal, ctx, Operations.Rename, req.HostId, req.ShareId, req.OldPath, AuditResults.Failure, "denied", durationMs: sw.ElapsedMilliseconds, ct: ct); return auth.Failure; }
            if (await perms.IsPermissionRootAsync(auth.UserId, req.ShareId, auth.NormalizedPath, ct))
            {
                await audit.LogAsync(principal, ctx, Operations.Rename, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Failure, "permission_root", durationMs: sw.ElapsedMilliseconds, usedPermissionId: auth.PermissionId, ct: ct);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var newNorm = PathHelper.NormalizePath(req.NewPath);
            var oldParent = PathHelper.GetParent(auth.NormalizedPath);
            var newParent = PathHelper.GetParent(newNorm);
            if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
            {
                await audit.LogAsync(principal, ctx, Operations.Rename, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Failure, "parent_changed", targetPath: newNorm, durationMs: sw.ElapsedMilliseconds, usedPermissionId: auth.PermissionId, ct: ct);
                return Results.BadRequest(new { error = "リネームでは親ディレクトリを変更できません" });
            }
            var (newAllowed, _) = await perms.CanPerformAsync(auth.UserId, req.ShareId, newNorm, Operations.Write, ct);
            if (!newAllowed) { await audit.LogAsync(principal, ctx, Operations.Rename, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Failure, "denied", targetPath: newNorm, durationMs: sw.ElapsedMilliseconds, usedPermissionId: auth.PermissionId, ct: ct); return Results.StatusCode(StatusCodes.Status403Forbidden); }

            var execCtx = await BuildExecutionContextAsync(db, enc, req.HostId, req.ShareId, ct);
            if (execCtx is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });
            try { await router.RenameAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, newNorm, ct); }
            catch (NodeUnreachableException nue) { await audit.LogAsync(principal, ctx, Operations.Rename, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Failure, nue.Message, targetPath: newNorm, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (Exception ex) { await audit.LogAsync(principal, ctx, Operations.Rename, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Failure, ex.Message, targetPath: newNorm, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); throw; }
            await audit.LogAsync(principal, ctx, Operations.Rename, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Success, targetPath: newNorm, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct);
            return Results.NoContent();
        });

        group.MapPost("/mkdir", async (
            MkdirRequest req, HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var auth = await ResolveAuthAsync(principal, req.HostId, req.ShareId, req.Path, Operations.Write, db, perms, ct);
            if (auth.Failure is not null) { await audit.LogAsync(principal, ctx, Operations.Write, req.HostId, req.ShareId, req.Path, AuditResults.Failure, "denied", durationMs: sw.ElapsedMilliseconds, ct: ct); return auth.Failure; }
            var execCtx = await BuildExecutionContextAsync(db, enc, req.HostId, req.ShareId, ct);
            if (execCtx is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });
            try { await router.MkdirAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct); }
            catch (NodeUnreachableException nue) { await audit.LogAsync(principal, ctx, Operations.Write, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Failure, nue.Message, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (Exception ex) { await audit.LogAsync(principal, ctx, Operations.Write, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Failure, ex.Message, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct); throw; }
            await audit.LogAsync(principal, ctx, Operations.Write, req.HostId, req.ShareId, auth.NormalizedPath, AuditResults.Success, durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id, usedPermissionId: auth.PermissionId, ct: ct);
            return Results.NoContent();
        });

        return app;
    }

    internal record AuthCheck(int UserId, string NormalizedPath, IResult? Failure, int? PermissionId);
    internal record ExecutionContext(CifsConnectionInfo Info, ExecutionNode Node);

    internal static async Task<AuthCheck> ResolveAuthAsync(
        ClaimsPrincipal principal, int hostId, int shareId, string path, string operation,
        AppDbContext db, PermissionService perms, CancellationToken ct)
    {
        if (!int.TryParse(principal.FindFirst("uid")?.Value, out var userId))
            return new AuthCheck(0, "/", Results.Unauthorized(), null);
        var normalized = PathHelper.NormalizePath(path);
        var (allowed, pid) = await perms.CanPerformAsync(userId, shareId, normalized, operation, ct);
        if (!allowed)
            return new AuthCheck(userId, normalized, Results.StatusCode(StatusCodes.Status403Forbidden), null);
        return new AuthCheck(userId, normalized, null, pid);
    }

    internal static async Task<ExecutionContext?> BuildExecutionContextAsync(
        AppDbContext db, EncryptionService enc, int hostId, int shareId, CancellationToken ct)
    {
        var row = await (from h in db.CifsHosts
            join s in db.CifsShares on h.Id equals s.HostId
            join n in db.ExecutionNodes on h.ExecutionNodeId equals n.Id
            where h.Id == hostId && s.Id == shareId
            select new { Host = h, Share = s, Node = n }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var pw = enc.Decrypt(row.Host.CredPasswordEnc);
        var info = new CifsConnectionInfo(row.Host.HostAddress, row.Host.Port, row.Host.CredUsername, pw, row.Share.ShareName);
        return new ExecutionContext(info, row.Node);
    }

    internal sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _readSide;
        public long BytesWritten { get; private set; }
        public CountingStream(Stream inner, bool readSide = false) { _inner = inner; _readSide = readSide; }
        public override bool CanRead => _readSide;
        public override bool CanSeek => false;
        public override bool CanWrite => !_readSide;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) { var n = _inner.Read(buffer, offset, count); BytesWritten += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var n = await _inner.ReadAsync(buffer, cancellationToken); BytesWritten += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); BytesWritten += count; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { await _inner.WriteAsync(buffer, cancellationToken); BytesWritten += buffer.Length; }
    }
}
