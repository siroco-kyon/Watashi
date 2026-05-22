using System.Text.Json.Serialization;

namespace Watashi.Shared.Models;

public class TrustedDevice
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string WindowsUsername { get; set; } = string.Empty;
    [JsonIgnore] public string DeviceTokenHash { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool IsRevoked { get; set; }
    public string? RevokedReason { get; set; }
    public DateTime? RevokedAt { get; set; }
}
