namespace Watashi.Shared.DTOs.Files;

public sealed record RemoteTrashEntryDto
{
    public Guid Id { get; init; }
    public int HostId { get; init; }
    public int ShareId { get; init; }
    public string OriginalPath { get; init; } = "/";
    public string Name { get; init; } = string.Empty;
    public string ItemType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime? OriginalModifiedAtUtc { get; init; }
    public string DeletedByUsername { get; init; } = string.Empty;
    public DateTime DeletedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? RestoredPath { get; init; }
}

public sealed record RemoteTrashListResponse
{
    public IReadOnlyList<RemoteTrashEntryDto> Items { get; init; } = Array.Empty<RemoteTrashEntryDto>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public long TotalBytes { get; init; }
}

public sealed record RestoreRemoteTrashRequest
{
    /// <summary>fail (既定) / rename / overwrite。overwrite は管理者だけが指定できる。</summary>
    public string CollisionPolicy { get; init; } = "fail";
}

public sealed record RestoreRemoteTrashResponse
{
    public RemoteTrashEntryDto Entry { get; init; } = new();
    public string RestoredPath { get; init; } = "/";
    public bool AlreadyCompleted { get; init; }
}
