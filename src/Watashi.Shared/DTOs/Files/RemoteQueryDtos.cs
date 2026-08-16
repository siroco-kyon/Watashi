namespace Watashi.Shared.DTOs.Files;

/// <summary>
/// A bounded page from an immutable directory snapshot.  The cursor is opaque and must be
/// sent back unchanged; callers must start a new query after it expires.
/// </summary>
public sealed class IncrementalFileListResponse
{
    public string CurrentPath { get; set; } = "/";
    public List<FileEntry> Entries { get; set; } = new();
    public bool CanGoUp { get; set; }
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
    public int LoadedCount { get; set; }
    public int TotalCount { get; set; }
    public DateTime CursorExpiresAt { get; set; }
    public bool Truncated { get; set; }
    public string? TruncationReason { get; set; }
}

public sealed class RemoteSearchRequest
{
    public string? Query { get; set; }
    public string? Cursor { get; set; }
    public int? Limit { get; set; }
    public int? MaxScanned { get; set; }
    public int? MaxResults { get; set; }
    public int? TimeoutSeconds { get; set; }
}

public sealed class RemoteSearchResponse
{
    public string Query { get; set; } = string.Empty;
    public List<RemoteSearchResult> Results { get; set; } = new();
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
    public int LoadedCount { get; set; }
    public int ScannedCount { get; set; }
    public int MatchedCount { get; set; }
    public DateTime CursorExpiresAt { get; set; }
    public bool Truncated { get; set; }
    public string? TruncationReason { get; set; }
    public List<RemoteQueryWarning> Warnings { get; set; } = new();
}

public sealed class RemoteSearchResult
{
    public int PermissionId { get; set; }
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string LocationRoot { get; set; } = "/";
    public string FullPath { get; set; } = "/";
    public string ParentPath { get; set; } = "/";
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long? Size { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public bool IsReparsePoint { get; set; }

    public string LocationLabel => $"{HostName} / {ShareName}";
}

public sealed class RemoteQueryWarning
{
    public string Code { get; set; } = string.Empty;
    public int? PermissionId { get; set; }
    public string Message { get; set; } = string.Empty;
}
