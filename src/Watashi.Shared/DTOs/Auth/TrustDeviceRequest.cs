namespace Watashi.Shared.DTOs.Auth;

public class TrustDeviceRequest
{
    public string MachineName { get; set; } = string.Empty;
    public string WindowsUsername { get; set; } = string.Empty;
}

public class TrustDeviceResponse
{
    public string DeviceToken { get; set; } = string.Empty;
}

public sealed class TrustedDeviceSelfDto
{
    public int Id { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string WindowsUsername { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool IsRevoked { get; set; }
    public string? RevokedReason { get; set; }
    public DateTime? RevokedAt { get; set; }
}
