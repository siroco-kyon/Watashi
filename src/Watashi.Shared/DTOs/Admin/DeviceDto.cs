namespace Watashi.Shared.DTOs.Admin;

public class DeviceDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string? Username { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string WindowsUsername { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool IsRevoked { get; set; }
    public string? RevokedReason { get; set; }
    public DateTime? RevokedAt { get; set; }
}
