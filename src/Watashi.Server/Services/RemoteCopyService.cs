using Watashi.Server.Endpoints;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Services;

public sealed class RemoteCopyException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }

    public RemoteCopyException(string code, string message, int statusCode = StatusCodes.Status409Conflict)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}

/// <summary>
/// コピー先と同じ共有に一時項目を作り、完了時のrenameで初めて公開する。
/// 元項目は一切変更しない。リパースポイントはリンク先へ入らず拒否する。
/// </summary>
public sealed class RemoteCopyService
{
    private const int MaxDepth = 64;
    private const int MaxItems = 100_000;
    private static readonly KeyedAsyncLock<string> TargetLocks = new();
    private readonly NodeRouter _router;

    public RemoteCopyService(NodeRouter router) => _router = router;

    internal async Task<RemoteCopyResponse> CopyAsync(
        RemoteCopyRequest request,
        FileEndpoints.ExecutionContext sourceContext,
        FileEndpoints.ExecutionContext targetContext,
        CancellationToken ct)
    {
        var source = RemoteTrashPathPolicy.NormalizeUserPath(request.SourcePath);
        var requestedTarget = RemoteTrashPathPolicy.NormalizeUserPath(request.TargetPath);
        var policy = TrashCollisionPolicies.Normalize(request.CollisionPolicy);

        if (request.SourceHostId == request.TargetHostId &&
            request.SourceShareId == request.TargetShareId &&
            (string.Equals(source, requestedTarget, StringComparison.OrdinalIgnoreCase) ||
             PathHelper.IsPathWithin(source, requestedTarget)))
        {
            throw new RemoteCopyException("target_inside_source",
                "コピー元自身またはコピー元フォルダーの配下にはコピーできません。",
                StatusCodes.Status400BadRequest);
        }

        var resourceKey = $"{request.TargetHostId}:{request.TargetShareId}:{requestedTarget.ToUpperInvariant()}";
        using var targetLease = await TargetLocks.AcquireAsync(resourceKey, ct);

        var sourceMeta = await _router.GetTransferMetadataAsync(
            sourceContext.Node, sourceContext.Info, source, ct);
        if (!sourceMeta.Exists)
            throw new RemoteCopyException("source_not_found", "コピー元が見つかりません。",
                StatusCodes.Status404NotFound);
        if (sourceMeta.IsReparsePoint)
            throw new RemoteCopyException("reparse_point_rejected",
                "リパースポイントはコピーできません。");

        var (target, renamed) = await ResolveTargetAsync(
            requestedTarget, policy, sourceMeta.Type, targetContext, ct);

        if (sourceMeta.Type == TransferFileTypes.File)
        {
            await using var input = await _router.OpenReadAsync(
                sourceContext.Node, sourceContext.Info, source, ct);
            await _router.UploadAsync(targetContext.Node, targetContext.Info, target, input, ct);
            return new RemoteCopyResponse
            {
                SourcePath = source,
                TargetPath = target,
                ItemType = FileEntryTypes.File,
                ItemCount = 1,
                BytesCopied = sourceMeta.Size ?? 0,
                RenamedForCollision = renamed,
            };
        }
        if (sourceMeta.Type != TransferFileTypes.Directory)
            throw new RemoteCopyException("unsupported_type", "この種類の項目はコピーできません。");
        if (policy == TrashCollisionPolicies.Overwrite)
            throw new RemoteCopyException("directory_overwrite_rejected",
                "事故防止のため、既存フォルダーへの上書きコピーはできません。別名コピーを選択してください。",
                StatusCodes.Status400BadRequest);

        var parent = PathHelper.GetParent(target);
        var temp = PathHelper.NormalizePath($"{parent}/.watashi-copy-{Guid.NewGuid():N}.tmpdir");
        var count = 1;
        long bytes = 0;
        try
        {
            await _router.MkdirAsync(targetContext.Node, targetContext.Info, temp, ct);
            var pending = new Stack<(string Source, string Target, int Depth)>();
            pending.Push((source, temp, 0));
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = pending.Pop();
                if (current.Depth > MaxDepth)
                    throw new RemoteCopyException("depth_limit", $"フォルダー階層が上限（{MaxDepth}）を超えました。");

                var entries = await _router.ListAsync(
                    sourceContext.Node, sourceContext.Info, current.Source, ct);
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (++count > MaxItems)
                        throw new RemoteCopyException("item_limit", $"コピー項目数が上限（{MaxItems:N0}）を超えました。");
                    if (entry.IsReparsePoint)
                        throw new RemoteCopyException("reparse_point_rejected",
                            $"リパースポイントを含むためコピーを中止しました: {entry.Name}");

                    var childSource = Join(current.Source, entry.Name);
                    var childTarget = Join(current.Target, entry.Name);
                    if (entry.Type == FileEntryTypes.Directory)
                    {
                        await _router.MkdirAsync(targetContext.Node, targetContext.Info, childTarget, ct);
                        pending.Push((childSource, childTarget, current.Depth + 1));
                    }
                    else if (entry.Type == FileEntryTypes.File)
                    {
                        await using var input = await _router.OpenReadAsync(
                            sourceContext.Node, sourceContext.Info, childSource, ct);
                        await _router.UploadAsync(targetContext.Node, targetContext.Info, childTarget, input, ct);
                        bytes = checked(bytes + (entry.Size ?? 0));
                    }
                    else
                    {
                        throw new RemoteCopyException("unsupported_type",
                            $"未対応の項目を含むためコピーを中止しました: {entry.Name}");
                    }
                }
            }

            await _router.RenameAsync(targetContext.Node, targetContext.Info, temp, target, ct);
            temp = string.Empty;
            return new RemoteCopyResponse
            {
                SourcePath = source,
                TargetPath = target,
                ItemType = FileEntryTypes.Directory,
                ItemCount = count,
                BytesCopied = bytes,
                RenamedForCollision = renamed,
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(temp))
            {
                try { await _router.DeleteAsync(targetContext.Node, targetContext.Info, temp, CancellationToken.None); }
                catch { /* 元のコピー失敗を優先。管理者は一時項目を通常の共有管理で回収できる。 */ }
            }
        }
    }

    private async Task<(string Path, bool Renamed)> ResolveTargetAsync(
        string requested,
        string policy,
        string sourceType,
        FileEndpoints.ExecutionContext targetContext,
        CancellationToken ct)
    {
        var metadata = await _router.GetTransferMetadataAsync(
            targetContext.Node, targetContext.Info, requested, ct);
        if (!metadata.Exists) return (requested, false);
        if (metadata.IsReparsePoint)
            throw new RemoteCopyException("reparse_point_rejected", "コピー先がリパースポイントです。");
        if (policy == TrashCollisionPolicies.Fail)
            throw new RemoteCopyException("target_exists", "コピー先に同名の項目があります。");
        if (policy == TrashCollisionPolicies.Overwrite)
        {
            if (sourceType != TransferFileTypes.File || metadata.Type != TransferFileTypes.File)
                throw new RemoteCopyException("type_conflict", "ファイル以外は上書きコピーできません。");
            return (requested, false);
        }

        var parent = PathHelper.GetParent(requested);
        var name = requested[(requested.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;
        for (var i = 1; i <= 10_000; i++)
        {
            var candidate = Join(parent, $"{stem} ({i}){extension}");
            var candidateMeta = await _router.GetTransferMetadataAsync(
                targetContext.Node, targetContext.Info, candidate, ct);
            if (!candidateMeta.Exists) return (candidate, true);
        }
        throw new RemoteCopyException("rename_exhausted", "コピー先の別名を確保できませんでした。");
    }

    private static string Join(string parent, string name)
        => PathHelper.NormalizePath(parent == "/" ? $"/{name}" : $"{parent}/{name}");
}
