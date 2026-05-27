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
            return await ExecuteAsync(audit, principal, ctx, Operations.Read, hostId, shareId, path ?? "/",
                db, enc, perms, ct, async (auth, execCtx) =>
            {
                var entries = (await router.ListAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct)).ToList();
                entries = (sort ?? "name") switch
                {
                    "name_desc" => entries.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                    "date" => entries.OrderBy(e => e.ModifiedAt).ToList(),
                    "date_desc" => entries.OrderByDescending(e => e.ModifiedAt).ToList(),
                    "size" => entries.OrderBy(e => e.Size ?? -1).ToList(),
                    "size_desc" => entries.OrderByDescending(e => e.Size ?? -1).ToList(),
                    _ => entries.OrderBy(e => e.Type == FileEntryTypes.Directory ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                };
                int p = Math.Max(1, page ?? 1);
                int total = entries.Count;
                var paged = entries.Skip((p - 1) * PageSize).Take(PageSize).ToList();
                bool isRoot = await perms.IsPermissionRootAsync(auth.UserId, shareId, auth.NormalizedPath, ct);
                var parent = new FileEntry { Name = "..", Type = FileEntryTypes.Parent, CanGoUp = !isRoot && auth.NormalizedPath != "/" };
                var response = new FileListResponse
                {
                    CurrentPath = auth.NormalizedPath,
                    Entries = new List<FileEntry>(paged.Count + 1) { parent }.Concat(paged).ToList(),
                    Page = p,
                    TotalCount = total,
                };
                return Results.Ok(response);
            });
        });

        group.MapGet("/download", async (
            int hostId, int shareId, string path,
            HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            return await ExecuteAsync(audit, principal, ctx, Operations.Read, hostId, shareId, path,
                db, enc, perms, ct, async (auth, execCtx) =>
            {
                var fileName = Path.GetFileName(auth.NormalizedPath);
                ctx.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
                ctx.Response.ContentType = "application/octet-stream";
                await using var stream = await router.OpenReadAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct);
                var counting = new CountingStream(ctx.Response.Body);
                await stream.CopyToAsync(counting, 4 * 1024 * 1024, ct);
                ctx.Items["bytes"] = counting.BytesWritten;
                return Results.Empty;
            });
        });

        group.MapPost("/upload", async (
            int hostId, int shareId, string path,
            HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            return await ExecuteAsync(audit, principal, ctx, Operations.Write, hostId, shareId, path,
                db, enc, perms, ct, async (auth, execCtx) =>
            {
                var counting = new CountingStream(ctx.Request.Body, readSide: true);
                await router.UploadAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, counting, ct);
                ctx.Items["bytes"] = counting.BytesWritten;
                return Results.NoContent();
            });
        });

        group.MapDelete("/", async (
            int hostId, int shareId, string path,
            HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            return await ExecuteAsync(audit, principal, ctx, Operations.Delete, hostId, shareId, path,
                db, enc, perms, ct, async (auth, execCtx) =>
            {
                if (await perms.IsPermissionRootAsync(auth.UserId, shareId, auth.NormalizedPath, ct))
                    return new FailureResult(PermissionDenied("許可ルート自体は削除できません。"), "permission_root");
                await router.DeleteAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct);
                return Results.NoContent();
            });
        });

        group.MapPost("/rename", async (
            RenameRequest req, HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            return await ExecuteAsync(audit, principal, ctx, Operations.Rename, req.HostId, req.ShareId, req.OldPath,
                db, enc, perms, ct, async (auth, execCtx) =>
            {
                if (await perms.IsPermissionRootAsync(auth.UserId, req.ShareId, auth.NormalizedPath, ct))
                    return new FailureResult(PermissionDenied("許可ルート自体はリネームできません。"), "permission_root");
                var newNorm = PathHelper.NormalizePath(req.NewPath);
                var oldParent = PathHelper.GetParent(auth.NormalizedPath);
                var newParent = PathHelper.GetParent(newNorm);
                if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Items["targetPath"] = newNorm;
                    return new FailureResult(Results.BadRequest(new { error = "リネームでは親ディレクトリを変更できません" }), "parent_changed");
                }
                var (newAllowed, _) = await perms.CanPerformAsync(auth.UserId, req.ShareId, newNorm, Operations.Write, ct);
                if (!newAllowed)
                {
                    ctx.Items["targetPath"] = newNorm;
                    return new FailureResult(PermissionDenied("リネーム先への書き込み権限がありません。"), "denied");
                }
                ctx.Items["targetPath"] = newNorm;
                await router.RenameAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, newNorm, ct);
                return Results.NoContent();
            });
        });

        group.MapPost("/mkdir", async (
            MkdirRequest req, HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            return await ExecuteAsync(audit, principal, ctx, Operations.Write, req.HostId, req.ShareId, req.Path,
                db, enc, perms, ct, async (auth, execCtx) =>
            {
                await router.MkdirAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct);
                return Results.NoContent();
            });
        });

        return app;
    }

    internal record AuthCheck(int UserId, string NormalizedPath, IResult? Failure, int? PermissionId);
    internal record ExecutionContext(CifsConnectionInfo Info, ExecutionNode Node);
    internal record FailureResult(IResult Result, string Reason);

    /// <summary>
    /// すべての file 操作で共通の手順 (auth → context 解決 → 操作 → audit) を 1 箇所に集約する。
    /// body は AuthCheck と ExecutionContext を受け取り、IResult もしくは FailureResult を返す。
    /// </summary>
    internal static async Task<IResult> ExecuteAsync(
        AuditLogService audit, ClaimsPrincipal principal, HttpContext ctx,
        string operation, int hostId, int shareId, string path,
        AppDbContext db, EncryptionService enc, PermissionService perms,
        CancellationToken ct,
        Func<AuthCheck, ExecutionContext, Task<object>> body)
    {
        var sw = Stopwatch.StartNew();
        var auth = await ResolveAuthAsync(principal, hostId, shareId, path, operation, perms);
        if (auth.Failure is not null)
        {
            await audit.LogAsync(principal, ctx, operation, hostId, shareId, path,
                AuditResults.Failure, "denied", durationMs: sw.ElapsedMilliseconds, ct: ct);
            return auth.Failure;
        }

        var execCtx = await BuildExecutionContextAsync(db, enc, hostId, shareId, ct);
        if (execCtx is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });

        try
        {
            var raw = await body(auth, execCtx);
            if (raw is FailureResult fr)
            {
                var tgt = ctx.Items.TryGetValue("targetPath", out var t) ? t as string : null;
                await audit.LogAsync(principal, ctx, operation, hostId, shareId, auth.NormalizedPath,
                    AuditResults.Failure, fr.Reason, targetPath: tgt,
                    durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id,
                    usedPermissionId: auth.PermissionId, ct: ct);
                return fr.Result;
            }
            var bytes = ctx.Items.TryGetValue("bytes", out var b) ? b as long? : null;
            var targetPath = ctx.Items.TryGetValue("targetPath", out var t2) ? t2 as string : null;
            await audit.LogAsync(principal, ctx, operation, hostId, shareId, auth.NormalizedPath,
                AuditResults.Success, targetPath: targetPath,
                bytesTransferred: bytes,
                durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id,
                usedPermissionId: auth.PermissionId, ct: ct);
            return (IResult)raw;
        }
        catch (NodeUnreachableException nue)
        {
            var bytes = ctx.Items.TryGetValue("bytes", out var b) ? b as long? : null;
            await audit.LogAsync(principal, ctx, operation, hostId, shareId, auth.NormalizedPath,
                AuditResults.Failure, nue.Message, bytesTransferred: bytes,
                durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id,
                usedPermissionId: auth.PermissionId, ct: ct);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            var bytes = ctx.Items.TryGetValue("bytes", out var b) ? b as long? : null;
            await audit.LogAsync(principal, ctx, operation, hostId, shareId, auth.NormalizedPath,
                AuditResults.Failure, ex.Message, bytesTransferred: bytes,
                durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id,
                usedPermissionId: auth.PermissionId, ct: ct);
            // 内部メッセージはクライアントに直接返さず Problem としてマスクする。
            return Results.Problem(detail: "内部エラーが発生しました。", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    internal static async Task<AuthCheck> ResolveAuthAsync(
        ClaimsPrincipal principal, int hostId, int shareId, string path, string operation,
        PermissionService perms, CancellationToken ct = default)
    {
        if (!principal.TryGetUserId(out var userId))
            return new AuthCheck(0, "/", Results.Unauthorized(), null);
        var normalized = PathHelper.NormalizePath(path);
        var (allowed, pid) = await perms.CanPerformAsync(userId, shareId, normalized, operation, ct);
        if (!allowed)
            return new AuthCheck(userId, normalized, PermissionDenied(GetPermissionDeniedMessage(operation)), null);
        return new AuthCheck(userId, normalized, null, pid);
    }

    private static IResult PermissionDenied(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);

    private static string GetPermissionDeniedMessage(string operation) => operation switch
    {
        Operations.Read => "読み取り権限がありません。",
        Operations.Write => "書き込み権限がありません。",
        Operations.Delete => "削除権限がありません。",
        Operations.Rename => "リネーム権限がありません。",
        _ => "権限がありません。",
    };

    internal static async Task<ExecutionContext?> BuildExecutionContextAsync(
        AppDbContext db, EncryptionService enc, int hostId, int shareId, CancellationToken ct)
    {
        var row = await (from h in db.CifsHosts.AsNoTracking()
            join s in db.CifsShares on h.Id equals s.HostId
            join n in db.ExecutionNodes.Include(x => x.GatewayNode) on h.ExecutionNodeId equals n.Id
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
