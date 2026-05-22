namespace Watashi.Shared.DTOs.Auth;

public class RefreshResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public bool MustChangePassword { get; set; }
}
