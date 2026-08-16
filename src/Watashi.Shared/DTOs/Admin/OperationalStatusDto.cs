namespace Watashi.Shared.DTOs.Admin;

public static class DiagnosticStatuses
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Unhealthy = "unhealthy";
    public const string NotConfigured = "not_configured";
}

/// <summary>管理画面に表示する、中央サーバーと依存先の運用診断スナップショット。</summary>
public class OperationalStatusDto
{
    public string Status { get; set; } = DiagnosticStatuses.Healthy;
    public DateTime CheckedAt { get; set; }
    public DiagnosticItemDto Database { get; set; } = new();
    public DiagnosticItemDto AuditOutbox { get; set; } = new();
    public DiagnosticItemDto Disk { get; set; } = new();
    public DiagnosticItemDto Backup { get; set; } = new();
    public DiagnosticItemDto Certificate { get; set; } = new();
    public List<NodeDiagnosticDto> Nodes { get; set; } = new();
}

public class DiagnosticItemDto
{
    public string Status { get; set; } = DiagnosticStatuses.Healthy;
    public string Message { get; set; } = string.Empty;
    public long? Value { get; set; }
    public DateTime? ObservedAt { get; set; }
}

public class NodeDiagnosticDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string Status { get; set; } = DiagnosticStatuses.Healthy;
    public DateTime? LastHeartbeatAt { get; set; }
    public string? EndpointScheme { get; set; }
    public int MaxConcurrency { get; set; }
    public string Message { get; set; } = string.Empty;
}
