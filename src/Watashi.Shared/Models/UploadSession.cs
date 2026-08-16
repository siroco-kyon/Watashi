namespace Watashi.Shared.Models;

/// <summary>
/// SMB 上の同一親ディレクトリに置いた一時ファイルと、再開位置を結び付ける永続 session。
/// IdempotencyKeyHash だけを保存し、呼び出し元が渡した key 自体はログ/DBへ残さない。
/// </summary>
public sealed class UploadSession
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public CifsShare? Share { get; set; }
    public string TargetPath { get; set; } = "/";
    public string TempPath { get; set; } = "/";
    public string IdempotencyKeyHash { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public string ExpectedSha256 { get; set; } = string.Empty;
    public long UploadedOffset { get; set; }
    public bool Overwrite { get; set; }

    // create 時点の target を記録し、complete 時の外部変更を size/mtime で検出する。
    public bool TargetExisted { get; set; }
    public long? TargetSize { get; set; }
    public DateTime? TargetModifiedAtUtc { get; set; }

    public string Status { get; set; } = UploadSessionStatuses.Active;
    public string? ErrorCode { get; set; }
    public string? CommittedETag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class UploadSessionStatuses
{
    public const string Active = "active";
    public const string Committing = "committing";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string Failed = "failed";
}
