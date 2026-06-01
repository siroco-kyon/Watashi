namespace Watashi.Shared.DTOs.Auth;

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    /// <summary>クライアントの Windows ログオンユーザー名 (運用上 Watashi ユーザー名と一致させる)。監査・別人ログイン検知用。任意。</summary>
    public string? WindowsUsername { get; set; }
    /// <summary>クライアントのマシン名。監査・端末変更検知用。任意。</summary>
    public string? MachineName { get; set; }
}
