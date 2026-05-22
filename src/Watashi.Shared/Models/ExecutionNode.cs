namespace Watashi.Shared.Models;

public class ExecutionNode
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? ClientCertificateThumbprint { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastHeartbeatAt { get; set; }
    public string HealthStatus { get; set; } = "Unknown";
    public int MaxConcurrency { get; set; } = 20;
    public DateTime CreatedAt { get; set; }
}
