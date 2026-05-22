namespace Watashi.Shared.DTOs.Auth;

public class AutoLoginRequest
{
    public string MachineName { get; set; } = string.Empty;
    public string WindowsUsername { get; set; } = string.Empty;
    public string DeviceToken { get; set; } = string.Empty;
}
