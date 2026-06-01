using System.Text.Json.Serialization;

namespace Watashi.Shared.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    [JsonIgnore] public string PasswordHash { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsLocked { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime PasswordChangedAt { get; set; }
    public DateTime PasswordExpiresAt { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>最後にログインした際にクライアントが申告した Windows ユーザー名 (別人ログイン検知用)。</summary>
    public string? LastWindowsUsername { get; set; }
    /// <summary>最後にログインした際にクライアントが申告したマシン名 (端末変更検知用)。</summary>
    public string? LastMachineName { get; set; }
    public DateTime CreatedAt { get; set; }
}
