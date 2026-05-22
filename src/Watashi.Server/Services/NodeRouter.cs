using Watashi.Server.Services.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
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

    public async Task<IReadOnlyList<FileEntry>> ListAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        EnsureReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.List(info, path), ct);
        return await _forwarder.ListAsync(node, info, path, ct);
    }

    public async Task<Stream> OpenReadAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        EnsureReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            return _direct.OpenRead(info, path);
        return await _forwarder.OpenDownloadAsync(node, info, path, ct);
    }

    public async Task UploadAsync(ExecutionNode node, CifsConnectionInfo info, string path, Stream input, CancellationToken ct)
    {
        EnsureReachable(node);
        if (node.NodeType == NodeTypes.Direct)
        {
            await using var smb = _direct.OpenWrite(info, path);
            await input.CopyToAsync(smb, 4 * 1024 * 1024, ct);
        }
        else
        {
            await _forwarder.UploadAsync(node, info, path, input, ct);
        }
    }

    public async Task DeleteAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        EnsureReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.Delete(info, path), ct);
        else
            await _forwarder.DeleteAsync(node, info, path, ct);
    }

    public async Task RenameAsync(ExecutionNode node, CifsConnectionInfo info, string oldPath, string newPath, CancellationToken ct)
    {
        EnsureReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.Rename(info, oldPath, newPath), ct);
        else
            await _forwarder.RenameAsync(node, info, oldPath, newPath, ct);
    }

    public async Task MkdirAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        EnsureReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            await Task.Run(() => _direct.Mkdir(info, path), ct);
        else
            await _forwarder.MkdirAsync(node, info, path, ct);
    }

    public async Task<bool> TestAsync(ExecutionNode node, CifsConnectionInfo info, CancellationToken ct)
    {
        EnsureReachable(node);
        if (node.NodeType == NodeTypes.Direct)
            return await Task.Run(() => _direct.TestConnection(info), ct);
        return await _forwarder.TestAsync(node, info, ct);
    }
}
