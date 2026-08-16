namespace Watashi.Shared.Models;

public class UserPermission
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int ShareId { get; set; }
    public CifsShare? Share { get; set; }
    public int TemplateId { get; set; }
    public PermissionTemplate? Template { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
    /// <summary>この権限が有効になり始める UTC 日時。null は即時有効。</summary>
    public DateTime? ValidFrom { get; set; }
    /// <summary>この権限が失効する UTC 日時（この時刻を含まない）。null は無期限。</summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>付与・変更理由。</summary>
    public string? Reason { get; set; }
    /// <summary>チケット番号または申請番号。</summary>
    public string? TicketNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
}
