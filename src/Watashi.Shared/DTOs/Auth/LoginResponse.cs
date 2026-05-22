namespace Watashi.Shared.DTOs.Auth;

public class LoginResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string RefreshTokenId { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public bool MustChangePassword { get; set; }
    public int? PasswordExpiresInDays { get; set; }
    /// <summary>クライアントのアイドルタイムアウト分数（サーバ側 SystemSettings 由来）。</summary>
    public int IdleMinutes { get; set; } = 30;
}
