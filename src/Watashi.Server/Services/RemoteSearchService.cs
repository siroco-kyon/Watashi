using Watashi.Server.Data;
using Watashi.Server.Endpoints;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Services;

internal interface IRemoteDirectoryLister
{
    Task<IReadOnlyList<FileEntry>> ListAsync(RemoteQueryScope scope, string path, CancellationToken ct);
}

internal sealed class RemoteDirectoryLister : IRemoteDirectoryLister
{
    private readonly AppDbContext _db;
    private readonly EncryptionService _encryption;
    private readonly NodeRouter _router;
    private readonly Dictionary<(int HostId, int ShareId), FileEndpoints.ExecutionContext> _contexts = new();

    public RemoteDirectoryLister(AppDbContext db, EncryptionService encryption, NodeRouter router)
    {
        _db = db;
        _encryption = encryption;
        _router = router;
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(
        RemoteQueryScope scope,
        string path,
        CancellationToken ct)
    {
        if (RemoteTrashPathPolicy.IsReservedPath(path))
            throw new UnauthorizedAccessException("管理用ごみ箱領域は検索できません。");
        var key = (scope.HostId, scope.ShareId);
        if (!_contexts.TryGetValue(key, out var executionContext))
        {
            executionContext = await FileEndpoints.BuildExecutionContextAsync(
                _db, _encryption, scope.HostId, scope.ShareId, ct)
                ?? throw new ArgumentException("ホスト/共有が見つかりません。");
            _contexts.Add(key, executionContext);
        }

        return await _router.ListAsync(
            executionContext.Node,
            executionContext.Info,
            PathHelper.NormalizePath(path),
            ct);
    }
}

internal sealed class RemoteSearchService
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;
    public const int DefaultMaxScanned = 25_000;
    public const int MaxScanned = 100_000;
    public const int DefaultMaxResults = 2_000;
    public const int MaxResults = 2_000;
    public const int DefaultTimeoutSeconds = 15;
    public const int MaxTimeoutSeconds = 30;
    public const int MaxDepth = 64;
    public const int MaxListEntries = 100_000;

    private readonly IRemoteDirectoryLister _lister;

    public RemoteSearchService(IRemoteDirectoryLister lister)
    {
        _lister = lister;
    }

    internal async Task<RemoteSearchExecution> SearchAsync(
        IReadOnlyList<RemoteQueryScope> scopes,
        string query,
        int maxScanned,
        int maxResults,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var results = new List<RemoteSearchResult>(Math.Min(maxResults, 1024));
        var warnings = new List<RemoteQueryWarning>();
        var warnedPermissions = new HashSet<int>();
        var scanned = 0;
        var matched = 0;
        var truncated = false;
        string? truncationReason = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        foreach (var scope in scopes)
        {
            var pending = new Stack<SearchDirectory>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(new SearchDirectory(scope.RootPath, 0));

            while (pending.Count > 0)
            {
                if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested)
                {
                    truncated = true;
                    truncationReason = "timeout";
                    return Finish();
                }

                var directory = pending.Pop();
                if (!visited.Add(directory.Path)) continue;

                IReadOnlyList<FileEntry> rawEntries;
                try
                {
                    rawEntries = await _lister.ListAsync(scope, directory.Path, timeout.Token);
                }
                catch (OperationCanceledException) when (
                    timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    truncated = true;
                    truncationReason = "timeout";
                    return Finish();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsRecoverableScopeFailure(ex))
                {
                    // Do not expose the failing path, server address, exception, or directory counts.
                    // The permission id is safe because it came from this user's active location list.
                    if (warnedPermissions.Add(scope.PermissionId))
                    {
                        warnings.Add(new RemoteQueryWarning
                        {
                            Code = "scope_unavailable",
                            PermissionId = scope.PermissionId,
                            Message = "権限のある場所の一部を検索できませんでした。",
                        });
                    }
                    truncated = true;
                    truncationReason ??= "partial_failure";
                    continue;
                }

                if (timeout.IsCancellationRequested)
                {
                    if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                    truncated = true;
                    truncationReason = "timeout";
                    return Finish();
                }

                var entries = rawEntries
                    .Where(x => x.Type != FileEntryTypes.Parent && x.Name is not "." and not "..")
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Name, StringComparer.Ordinal)
                    .ToList();
                var childDirectories = new List<SearchDirectory>();

                foreach (var entry in entries)
                {
                    if (scanned >= maxScanned)
                    {
                        truncated = true;
                        truncationReason = "scan_limit";
                        return Finish();
                    }

                    scanned++;
                    // A compromised/misconfigured agent must not be able to make traversal escape the
                    // permission root by returning separator-bearing names.
                    if (!IsSafeChildName(entry.Name))
                        continue;

                    var fullPath = JoinPath(directory.Path, entry.Name);
                    if (RemoteTrashPathPolicy.IsReservedPath(fullPath)) continue;
                    if (!PathHelper.IsPathWithin(scope.RootPath, fullPath)) continue;

                    if ((scanned & 0xff) == 0)
                    {
                        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested)
                        {
                            truncated = true;
                            truncationReason = "timeout";
                            return Finish();
                        }
                    }
                    if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        matched++;
                        if (results.Count >= maxResults)
                        {
                            truncated = true;
                            truncationReason = "result_limit";
                            return Finish();
                        }
                        results.Add(new RemoteSearchResult
                        {
                            PermissionId = scope.PermissionId,
                            HostId = scope.HostId,
                            ShareId = scope.ShareId,
                            HostName = scope.HostName,
                            ShareName = scope.ShareName,
                            LocationRoot = scope.RootPath,
                            FullPath = fullPath,
                            ParentPath = directory.Path,
                            Name = entry.Name,
                            Type = entry.Type,
                            Size = entry.Size,
                            ModifiedAt = entry.ModifiedAt,
                            IsReparsePoint = entry.IsReparsePoint,
                        });
                    }

                    if (entry.Type == FileEntryTypes.Directory && !entry.IsReparsePoint)
                    {
                        if (directory.Depth >= MaxDepth)
                        {
                            truncated = true;
                            truncationReason ??= "depth_limit";
                        }
                        else
                        {
                            childDirectories.Add(new SearchDirectory(fullPath, directory.Depth + 1));
                        }
                    }
                }

                // Stack is LIFO; reverse insertion preserves the deterministic name order above.
                for (var i = childDirectories.Count - 1; i >= 0; i--)
                    pending.Push(childDirectories[i]);
            }
        }

        return Finish();

        RemoteSearchExecution Finish() => new(
            results,
            scanned,
            matched,
            truncated,
            truncationReason,
            warnings);
    }

    internal static IReadOnlyList<RemoteQueryScope> CoalesceReadableScopes(IEnumerable<LocationDto> locations)
    {
        var result = new List<RemoteQueryScope>();
        foreach (var group in locations
                     .Where(x => x.Permissions.Read &&
                                 !RemoteTrashPathPolicy.IsReservedPath(x.Path))
                     .GroupBy(x => (x.HostId, x.ShareId))
                     .OrderBy(x => x.Key.HostId)
                     .ThenBy(x => x.Key.ShareId))
        {
            var selected = new List<LocationDto>();
            foreach (var location in group
                         .OrderBy(x => PathHelper.NormalizePath(x.Path).Length)
                         .ThenBy(x => PathHelper.NormalizePath(x.Path), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.PermissionId))
            {
                var normalized = PathHelper.NormalizePath(location.Path);
                if (selected.Any(existing => PathHelper.IsPathWithin(existing.Path, normalized)))
                    continue;
                selected.Add(location);
                result.Add(new RemoteQueryScope(
                    location.PermissionId,
                    location.HostId,
                    location.ShareId,
                    normalized,
                    location.HostName,
                    location.ShareName));
            }
        }
        return result;
    }

    internal static bool IsScopeStillAuthorized(
        RemoteQueryScope scope,
        IEnumerable<LocationDto> activeLocations)
        => activeLocations.Any(location =>
            location.PermissionId == scope.PermissionId &&
            location.HostId == scope.HostId &&
            location.ShareId == scope.ShareId &&
            location.Permissions.Read &&
            string.Equals(
                PathHelper.NormalizePath(location.Path),
                scope.RootPath,
                StringComparison.OrdinalIgnoreCase));

    internal static bool IsSafeChildName(string name)
        => !string.IsNullOrWhiteSpace(name) &&
           name is not "." and not ".." &&
           !name.Contains('/') &&
           !name.Contains('\\');

    private static bool IsRecoverableScopeFailure(Exception ex) => ex is
        NodeUnreachableException or
        AgentRelayException or
        IOException or
        UnauthorizedAccessException or
        ArgumentException or
        HttpRequestException;

    private static string JoinPath(string parent, string name)
    {
        var normalized = PathHelper.NormalizePath(parent);
        return normalized == "/" ? "/" + name : normalized + "/" + name;
    }

    private sealed record SearchDirectory(string Path, int Depth);
}

internal sealed record RemoteSearchExecution(
    IReadOnlyList<RemoteSearchResult> Results,
    int ScannedCount,
    int MatchedCount,
    bool Truncated,
    string? TruncationReason,
    IReadOnlyList<RemoteQueryWarning> Warnings);
