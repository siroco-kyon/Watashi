namespace Watashi.Shared.DTOs.Admin;

public class NodeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string? ClientCertificateThumbprint { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public string HealthStatus { get; set; } = "Unknown";
    public int MaxConcurrency { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateNodeRequest
{
    public string Name { get; set; } = string.Empty;
    public string NodeType { get; set; } = "Direct";
    public string? Endpoint { get; set; }
    public string? ClientCertificateThumbprint { get; set; }
    public int MaxConcurrency { get; set; } = 20;
}

public class UpdateNodeRequest
{
    public string? Name { get; set; }
    public string? Endpoint { get; set; }
    public string? ClientCertificateThumbprint { get; set; }
    public bool? IsActive { get; set; }
    public int? MaxConcurrency { get; set; }
}
