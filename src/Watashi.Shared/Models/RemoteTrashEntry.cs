namespace Watashi.Shared.Models;

/// <summary>共有内の管理用隔離領域へ原子的に移動したファイル／フォルダの永続台帳。</summary>
public sealed class RemoteTrashEntry
{
    public Guid Id { get; set; }
    public int? DeletedByUserId { get; set; }
    public User? DeletedByUser { get; set; }
    public string DeletedByUsername { get; set; } = string.Empty;
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public CifsShare? Share { get; set; }
    public string OriginalPath { get; set; } = "/";
    public string TrashPath { get; set; } = "/";
    public string ItemType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime? OriginalModifiedAtUtc { get; set; }
    public string Status { get; set; } = RemoteTrashStatuses.Trashing;
    public string? ErrorCode { get; set; }
    public DateTime DeletedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? RestoredAt { get; set; }
    public int? RestoredByUserId { get; set; }
    public string? RestoredPath { get; set; }
    public DateTime? PurgedAt { get; set; }
    public int? PurgedByUserId { get; set; }
}

public static class RemoteTrashStatuses
{
    public const string Trashing = "trashing";
    public const string Active = "active";
    public const string Restoring = "restoring";
    public const string Restored = "restored";
    public const string Purging = "purging";
    public const string Purged = "purged";
    public const string Failed = "failed";
}
