using System.Text.Json.Serialization;

namespace Watashi.Shared.Models;

public class CifsHost
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HostAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 445;
    public string? Description { get; set; }
    public string CredUsername { get; set; } = string.Empty;
    [JsonIgnore] public byte[] CredPasswordEnc { get; set; } = Array.Empty<byte>();
    public int ExecutionNodeId { get; set; }
    public ExecutionNode? ExecutionNode { get; set; }
    public DateTime CreatedAt { get; set; }
}
