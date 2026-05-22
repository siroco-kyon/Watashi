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
