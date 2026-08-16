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
                var entries = (await router.ListAsync(execCtx.Node, execCtx.Info, auth.NormalizedPath, ct))
                    .Where(e => !string.Equals(e.Name, RemoteTrashPathPolicy.RootName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                entries = FileEntrySort.Sort(entries, sort);
                // page <= 0 は「全件」。クライアントは結局全ページを取得するため、
                // ページ要求のたびに SMB 全列挙 + ソートを繰り返すより 1 回で返す方がはるかに速い。
                bool all = (page ?? 1) <= 0;
                int p = all ? 1 : Math.Max(1, page ?? 1);
                int total = entries.Count;
                var paged = all ? entries : entries.Skip((p - 1) * PageSize).Take(PageSize).ToList();
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
            }, auditOperation: Operations.List);
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
                // サイズが分かる場合は Content-Length を返す。クライアントの進捗表示が正確になり、
                // 転送途中でエラーが起きた場合も受信側が不完全なレスポンスとして確実に検知できる。
                try { ctx.Response.ContentLength = stream.Length; }
                catch (NotSupportedException) { /* チャンク転送のままにする */ }
                var counting = new CountingStream(ctx.Response.Body);
                await stream.CopyToAsync(counting, 4 * 1024 * 1024, ct);
                ctx.Items["bytes"] = counting.BytesWritten;
                return Results.Empty;
            }, auditOperation: Operations.Download);
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
            }, auditOperation: Operations.Upload);
        });

        group.MapDelete("/", async (
            int hostId, int shareId, string path,
            HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, RemoteTrashService trash,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            return await ExecuteAsync(audit, principal, ctx, Operations.Delete, hostId, shareId, path,
                db, enc, perms, ct, async (auth, execCtx) =>
            {
                if (await perms.IsPermissionRootAsync(auth.UserId, shareId, auth.NormalizedPath, ct))
                    return new FailureResult(PermissionDenied("許可ルート自体は削除できません。"), "permission_root");
                var result = await trash.TrashAsync(
                    auth.UserId,
                    principal.GetUsername() ?? $"user:{auth.UserId}",
                    hostId,
                    shareId,
                    auth.NormalizedPath,
                    execCtx.Node,
                    execCtx.Info,
                    auth.PermissionId,
                    ct);
                ctx.Items["targetPath"] = result.TargetPath;
                ctx.Items["bytes"] = result.Entry.SizeBytes;
                return Results.NoContent();
            }, auditOperation: Operations.Trash);
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
            }, auditOperation: Operations.Mkdir);
        });

        group.MapPost("/copy", async (
            RemoteCopyRequest req, HttpContext ctx, AppDbContext db, NodeRouter router, EncryptionService enc,
            PermissionService perms, AuditLogService audit, RemoteCopyService copy,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            return await ExecuteAsync(audit, principal, ctx, Operations.Read,
                req.SourceHostId, req.SourceShareId, req.SourcePath,
                db, enc, perms, ct, async (sourceAuth, sourceContext) =>
            {
                var targetAuth = await ResolveAuthAsync(principal,
                    req.TargetHostId, req.TargetShareId, req.TargetPath, Operations.Write, perms, ct);
                ctx.Items["targetPath"] = req.SourceHostId == req.TargetHostId &&
                    req.SourceShareId == req.TargetShareId
                        ? targetAuth.NormalizedPath
                        : $"host#{req.TargetHostId}/share#{req.TargetShareId}:{targetAuth.NormalizedPath}";
                if (targetAuth.Failure is not null)
                    return new FailureResult(targetAuth.Failure, "target_denied");

                var targetContext = await BuildExecutionContextAsync(
                    db, enc, req.TargetHostId, req.TargetShareId, ct);
                if (targetContext is null)
                    return new FailureResult(
                        Results.BadRequest(new { error = "コピー先ホスト/共有が見つかりません。" }),
                        "target_not_found");

                var result = await copy.CopyAsync(req with
                {
                    SourcePath = sourceAuth.NormalizedPath,
                    TargetPath = targetAuth.NormalizedPath,
                }, sourceContext, targetContext, ct);
                ctx.Items["targetPath"] = req.SourceHostId == req.TargetHostId &&
                    req.SourceShareId == req.TargetShareId
                        ? result.TargetPath
                        : $"host#{req.TargetHostId}/share#{req.TargetShareId}:{result.TargetPath}";
                ctx.Items["bytes"] = result.BytesCopied;
                return Results.Ok(result);
            }, auditOperation: Operations.Copy);
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
        Func<AuthCheck, ExecutionContext, Task<object>> body,
        string? auditOperation = null)
    {
        var sw = Stopwatch.StartNew();
        var logOperation = auditOperation ?? operation;
        // 監査ログの書き込みには常に CancellationToken.None を使う。リクエストの ct を渡すと、
        // クライアント切断時に SaveChanges ごとキャンセルされ、実際に行われた操作
        // (完了済みアップロード・権限拒否・中断された転送) の証跡が消えてしまう。
        var auth = await ResolveAuthAsync(principal, hostId, shareId, path, operation, perms, ct);
        if (auth.Failure is not null)
        {
            await audit.LogAsync(principal, ctx, logOperation, hostId, shareId, path,
                AuditResults.Failure, "denied", durationMs: sw.ElapsedMilliseconds, ct: CancellationToken.None);
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
                await audit.LogAsync(principal, ctx, logOperation, hostId, shareId, auth.NormalizedPath,
                    AuditResults.Failure, fr.Reason, targetPath: tgt,
                    durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id,
                    usedPermissionId: auth.PermissionId, ct: CancellationToken.None);
                return fr.Result;
            }
            var bytes = ctx.Items.TryGetValue("bytes", out var b) ? b as long? : null;
            var targetPath = ctx.Items.TryGetValue("targetPath", out var t2) ? t2 as string : null;
            await audit.LogAsync(principal, ctx, logOperation, hostId, shareId, auth.NormalizedPath,
                AuditResults.Success, targetPath: targetPath,
                bytesTransferred: bytes,
                durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id,
                usedPermissionId: auth.PermissionId, ct: CancellationToken.None);
            return (IResult)raw;
        }
        catch (Exception ex)
        {
            var bytes = ctx.Items.TryGetValue("bytes", out var b) ? b as long? : null;
            await audit.LogAsync(principal, ctx, logOperation, hostId, shareId, auth.NormalizedPath,
                AuditResults.Failure, ex is OperationCanceledException ? "canceled" : ex.Message,
                bytesTransferred: bytes,
                durationMs: sw.ElapsedMilliseconds, executionNodeId: execCtx.Node.Id,
                usedPermissionId: auth.PermissionId, ct: CancellationToken.None);
            // ダウンロード等でレスポンス送信開始後に失敗した場合、ステータスコードはもう
            // 変更できない (Results.Problem の実行が二次例外になる)。接続を切って
            // クライアント側に不完全なレスポンスとして検知させる。
            if (ctx.Response.HasStarted)
            {
                ctx.Abort();
                return Results.Empty;
            }
            return MapExecutionError(ex);
        }
    }

    /// <summary>
    /// 実行系の例外を安全な IResult にマッピングする。
    /// CIFS 由来の IOException / UnauthorizedAccessException はメッセージが安全なため
    /// クライアントへ具体的に返し、想定外の例外のみ汎用メッセージでマスクする。
    /// Agent 経由 (AgentRelayException) の場合も、Direct 経路と同じ意味のステータスコードに
    /// なるようマッピングする (Agent 自身が返した 503/502 等をそのまま伝える。汎用 500 に
    /// 丸めてしまうと呼び出し元がリトライ判断やユーザー向けメッセージを出し分けられない)。
    /// </summary>
    internal static IResult MapExecutionError(Exception ex) => ex switch
    {
        NodeUnreachableException => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
        RemoteTrashException trash => Results.Json(new { error = trash.Message, code = trash.Code },
            statusCode: trash.StatusCode),
        RemoteCopyException copy => Results.Json(new { error = copy.Message, code = copy.Code },
            statusCode: copy.StatusCode),
        AgentRelayException { StatusCode: StatusCodes.Status503ServiceUnavailable } are => WithRetryAfter(are.RetryAfter),
        AgentRelayException are => Results.Problem(detail: are.Message, statusCode: are.StatusCode),
        TransferOffsetMismatchException mismatch => Results.Json(new
        {
            error = mismatch.Message,
            code = "offset_mismatch",
            expectedOffset = mismatch.ExpectedOffset,
            actualOffset = mismatch.ActualOffset,
        }, statusCode: StatusCodes.Status409Conflict),
        TransferRangeNotSatisfiableException range => Results.Json(new
        {
            error = range.Message,
            code = "range_not_satisfiable",
            expectedOffset = range.Size,
            actualOffset = range.Offset,
        }, statusCode: StatusCodes.Status416RangeNotSatisfiable),
        TransferReparsePointException => Results.Json(new
        {
            error = ex.Message,
            code = "reparse_point_rejected",
        }, statusCode: StatusCodes.Status409Conflict),
        ArgumentException => Results.BadRequest(new { error = ex.Message }),
        UnauthorizedAccessException => Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway),
        IOException => Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Problem(detail: "内部エラーが発生しました。", statusCode: StatusCodes.Status500InternalServerError),
    };

    private static IResult WithRetryAfter(string? retryAfter) => new RetryAfterResult(retryAfter);

    /// <summary>503 応答に Retry-After ヘッダーを付与する。Agent 自身が返した Retry-After を
    /// そのまま中継できるよう、Results.StatusCode では届かないヘッダー設定をここで行う。</summary>
    private sealed class RetryAfterResult : IResult
    {
        private readonly string? _retryAfter;
        public RetryAfterResult(string? retryAfter) => _retryAfter = retryAfter;
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            if (!string.IsNullOrEmpty(_retryAfter))
                httpContext.Response.Headers.RetryAfter = _retryAfter;
            return Task.CompletedTask;
        }
    }

    internal static async Task<AuthCheck> ResolveAuthAsync(
        ClaimsPrincipal principal, int hostId, int shareId, string path, string operation,
        PermissionService perms, CancellationToken ct = default)
    {
        if (!principal.TryGetUserId(out var userId))
            return new AuthCheck(0, "/", Results.Unauthorized(), null);
        var normalized = PathHelper.NormalizePath(path);
        if (RemoteTrashPathPolicy.IsReservedPath(normalized))
            return new AuthCheck(userId, normalized, Results.NotFound(), null);
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
