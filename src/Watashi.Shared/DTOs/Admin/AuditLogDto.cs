namespace Watashi.Shared.DTOs.Admin;

public class AuditLogDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public int? HostId { get; set; }
    public int? ShareId { get; set; }
    public string? Path { get; set; }
    public string? TargetPath { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? ClientIp { get; set; }
    public string? ClientHostname { get; set; }
    public long? BytesTransferred { get; set; }
    public long? DurationMs { get; set; }
    public string? Protocol { get; set; }
    public int? ExecutionNodeId { get; set; }
    public int? UsedPermissionId { get; set; }
}
