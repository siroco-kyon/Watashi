namespace Watashi.Shared.DTOs.Auth;

public class RefreshRequest
{
    public string RefreshTokenId { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
}
