using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

public class NodeUnreachableException : Exception
{
    public ExecutionNode Node { get; }
    public NodeUnreachableException(ExecutionNode node)
        : base($"ExecutionNode '{node.Name}' (#{node.Id}) は到達不能です (HealthStatus={node.HealthStatus}, IsActive={node.IsActive}).")
    {
        Node = node;
    }
}

public class NodeRouter
{
    private readonly CifsService _direct;
    private readonly AgentForwarder _forwarder;

    public NodeRouter(CifsService direct, AgentForwarder forwarder)
    {
        _direct = direct;
        _forwarder = forwarder;
    }

    private static void EnsureReachable(ExecutionNode node)
    {
        if (!node.IsActive || node.HealthStatus == HealthStatuses.Unhealthy)
            throw new NodeUnreachableException(node);
    }

    private static void EnsureRouteReachable(ExecutionNode node)
    {
        // Gateway 経由の最終 Agent は中央へ直接 heartbeat できない構成があるため、
        // 到達性の事前判定は入口になる Gateway Agent に対して行う。
        EnsureReachable(node.GatewayNode ?? node);
    }

    public virtual async Task<IReadOnlyList<FileEntry>> ListAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        EnsureRouteReachable(node);
        path = TransferV2Validation.NormalizeAndValidateUserPath(path);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.List(info, path, ct), ct);
        return await _forwarder.ListAsync(node, info, path, ct);
    }

    public virtual async Task<Stream> OpenReadAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        EnsureRouteReachable(node);
        path = TransferV2Validation.NormalizeAndValidateUserPath(path);
        if (node.NodeType == NodeTypes.Direct)
            return _direct.OpenRead(info, path, ct);
        return await _forwarder.OpenDownloadAsync(node, info, path, ct);
    }

    public virtual async Task<TransferFileMetadata> GetTransferMetadataAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.GetTransferMetadata(info, path, ct), ct);
        return await _forwarder.GetTransferMetadataAsync(node, info, path, ct);
    }

    public virtual async Task<TransferFileMetadata> EnsureTempFileAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string tempPath,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        var normalizedPath = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.EnsureTempFile(info, normalizedPath, ct), ct);
        return await _forwarder.EnsureTempFileAsync(node, info, normalizedPath, ct);
    }

    public virtual async Task<Stream> OpenReadRangeAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        long offset,
        int length,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        TransferV2Validation.ValidateReadRange(offset, length);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.OpenReadRange(info, path, offset, length, ct), ct);
        return await _forwarder.OpenReadRangeAsync(node, info, path, offset, length, ct);
    }

    public virtual async Task<TransferReadChunk> ReadRangeChunkAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        long offset,
        int length,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        TransferV2Validation.ValidateReadRange(offset, length);
        if (node.NodeType != NodeTypes.Direct)
            return await _forwarder.ReadRangeChunkAsync(node, info, path, offset, length, ct);

        await using var stream = await Task.Run(() =>
            _direct.OpenReadRange(info, path, offset, length, ct), ct);
        var streamLength = checked((int)stream.Length);
        if (streamLength < 0 || streamLength > length)
            throw new InvalidDataException("Direct range stream の長さが不正です。");
        var data = new byte[streamLength];
        var read = 0;
        while (read < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(data.AsMemory(read), ct);
            if (count == 0)
                throw new EndOfStreamException(
                    $"Direct range stream が途中で終了しました (expected={data.Length}, actual={read})。");
            read += count;
        }
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, ct) != 0)
            throw new InvalidDataException("Direct range stream が宣言長を超えました。");
        return new TransferReadChunk(data, TransferHashing.ComputeSha256Hex(data));
    }

    public virtual async Task<TransferChunkWriteResult> WriteTempChunkAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string tempPath,
        long offset,
        ReadOnlyMemory<byte> chunk,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        var normalizedPath = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
        TransferV2Validation.ValidateChunk(offset, chunk.Length);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.WriteTempChunk(
                info, normalizedPath, offset, chunk, ct), ct);
        return await _forwarder.WriteTempChunkAsync(node, info, normalizedPath, offset, chunk, ct);
    }

    public virtual async Task<TransferSha256Result> ComputeSha256Async(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.ComputeSha256(info, path, ct), ct);
        return await _forwarder.ComputeSha256Async(node, info, path, ct);
    }

    public virtual async Task CommitTempAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string tempPath,
        string targetPath,
        bool replaceIfExists,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        var paths = TransferV2Validation.ValidateCommitPaths(tempPath, targetPath);
        if (node.NodeType == NodeTypes.Direct)
        {
            await Task.Run(() => _direct.CommitTemp(
                info, paths.TempPath, paths.TargetPath, replaceIfExists, ct), ct);
        }
        else
        {
            await _forwarder.CommitTempAsync(
                node, info, paths.TempPath, paths.TargetPath, replaceIfExists, ct);
        }
    }

    public virtual async Task DeleteTempAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string tempPath,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        var normalizedPath = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
        var metadata = await GetTransferMetadataAsync(node, info, normalizedPath, ct);
        if (!metadata.Exists) return;
        if (metadata.IsReparsePoint || metadata.Type != TransferFileTypes.File)
            throw new TransferReparsePointException(normalizedPath);
        await DeleteCoreAsync(node, info, normalizedPath, ct);
    }

    public virtual async Task<RemoteTrashItemMetadata> InspectForTrashAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        var normalized = RemoteTrashPathPolicy.NormalizeUserPath(path);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.InspectForTrash(info, normalized, ct), ct);
        return await _forwarder.InspectForTrashAsync(node, info, normalized, ct);
    }

    public virtual async Task MoveToTrashAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string sourcePath,
        string trashPath,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        var source = RemoteTrashPathPolicy.NormalizeUserPath(sourcePath);
        var target = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.MoveToTrash(info, source, target, ct), ct);
        else
            await _forwarder.MoveToTrashAsync(node, info, source, target, ct);
    }

    public virtual async Task RestoreFromTrashAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string trashPath,
        string targetPath,
        bool replaceIfExists,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        var source = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        var target = RemoteTrashPathPolicy.NormalizeUserPath(targetPath);
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.RestoreFromTrash(
                info, source, target, replaceIfExists, ct), ct);
        else
            await _forwarder.RestoreFromTrashAsync(
                node, info, source, target, replaceIfExists, ct);
    }

    public virtual async Task PurgeTrashItemAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string trashPath,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        var normalized = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.PurgeTrashItem(info, normalized, ct), ct);
        else
            await _forwarder.PurgeTrashItemAsync(node, info, normalized, ct);
    }

    public virtual async Task UploadAsync(ExecutionNode node, CifsConnectionInfo info, string path, Stream input, CancellationToken ct)
    {
        EnsureRouteReachable(node);
        path = TransferV2Validation.NormalizeAndValidateUserPath(path);
        var parent = PathHelper.GetParent(path);
        var tempPath = PathHelper.NormalizePath($"{parent}/.watashi-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await UploadCoreAsync(node, info, tempPath, input, ct);
            await RenameCoreAsync(node, info, tempPath, path, replaceIfExists: true, ct);
        }
        catch
        {
            try { await DeleteCoreAsync(node, info, tempPath, CancellationToken.None); }
            catch { /* 元の転送エラーを優先する。孤立一時ファイルは後から安全に削除できる。 */ }
            throw;
        }
    }

    /// <summary>
    /// 永続upload台帳が先に作成済みの一時パスへlegacy request bodyを書き込む。
    /// 呼び出し側がcommitと状態遷移を管理し、process停止時もjanitorがtempを回収できる。
    /// </summary>
    public virtual async Task WriteTempStreamAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string tempPath,
        Stream input,
        CancellationToken ct)
    {
        EnsureRouteReachable(node);
        tempPath = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
        await UploadCoreAsync(node, info, tempPath, input, ct);
    }

    private async Task UploadCoreAsync(ExecutionNode node, CifsConnectionInfo info, string path, Stream input, CancellationToken ct)
    {
        if (node.NodeType == NodeTypes.Direct)
        {
            await using var smb = _direct.OpenWrite(info, path, ct);
            await input.CopyToAsync(smb, 4 * 1024 * 1024, ct);
        }
        else
        {
            await _forwarder.UploadAsync(node, info, path, input, ct);
        }
    }

    public virtual async Task DeleteAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        EnsureRouteReachable(node);
        path = TransferV2Validation.NormalizeAndValidateUserPath(path);
        await DeleteCoreAsync(node, info, path, ct);
    }

    private async Task DeleteCoreAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.Delete(info, path, ct), ct);
        else
            await _forwarder.DeleteAsync(node, info, path, ct);
    }

    public virtual async Task RenameAsync(ExecutionNode node, CifsConnectionInfo info, string oldPath, string newPath, CancellationToken ct)
    {
        EnsureRouteReachable(node);
        oldPath = TransferV2Validation.NormalizeAndValidateUserPath(oldPath);
        newPath = TransferV2Validation.NormalizeAndValidateUserPath(newPath);
        await RenameCoreAsync(node, info, oldPath, newPath, replaceIfExists: false, ct);
    }

    private async Task RenameCoreAsync(ExecutionNode node, CifsConnectionInfo info, string oldPath, string newPath, bool replaceIfExists, CancellationToken ct)
    {
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.Rename(info, oldPath, newPath, replaceIfExists, ct), ct);
        else
            await _forwarder.RenameAsync(node, info, oldPath, newPath, ct, replaceIfExists);
    }

    public virtual async Task MkdirAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        EnsureRouteReachable(node);
        path = TransferV2Validation.NormalizeAndValidateUserPath(path);
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.Mkdir(info, path, ct), ct);
        else
            await _forwarder.MkdirAsync(node, info, path, ct);
    }

    public async Task<bool> TestAsync(ExecutionNode node, CifsConnectionInfo info, CancellationToken ct)
    {
        EnsureRouteReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.TestConnection(info, ct), ct);
        return await _forwarder.TestAsync(node, info, ct);
    }
}
