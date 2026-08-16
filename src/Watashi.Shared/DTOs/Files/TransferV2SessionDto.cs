namespace Watashi.Shared.DTOs.Files;

/// <summary>再開可能アップロード session を作成する要求。</summary>
public sealed record CreateUploadSessionRequest
{
    public int HostId { get; init; }
    public int ShareId { get; init; }
    public string Path { get; init; } = "/";
    public long TotalSize { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string IdempotencyKey { get; init; } = string.Empty;
    public bool Overwrite { get; init; }
}

/// <summary>永続化された再開可能アップロード session の公開状態。</summary>
public sealed record UploadSessionDto
{
    public Guid SessionId { get; init; }
    public string Status { get; init; } = string.Empty;
    public int HostId { get; init; }
    public int ShareId { get; init; }
    public string Path { get; init; } = "/";
    public long TotalSize { get; init; }
    public long UploadedOffset { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public bool Overwrite { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ETag { get; init; }
}

/// <summary>download の再開判定に使う、通常ファイル metadata と強い ETag。</summary>
public sealed record TransferDownloadMetadataDto
{
    public bool Exists { get; init; }
    public string Type { get; init; } = string.Empty;
    public long? Size { get; init; }
    public DateTime? ModifiedAtUtc { get; init; }
    public string? Sha256 { get; init; }
    public string? ETag { get; init; }
}

public static class TransferV2Headers
{
    public const string ChunkSha256 = "X-Chunk-SHA256";
}
