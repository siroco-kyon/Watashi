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
    /// <summary>
    /// 管理者が明示的に利用停止した状態。連続ログイン失敗による <see cref="IsLocked"/> とは別管理で、
    /// ロック解除やパスワード再設定を行っても自動的には解除されない。
    /// </summary>
    public bool IsDisabled { get; set; }
    /// <summary>最後に明示的な無効化を行った UTC 日時。</summary>
    public DateTime? DisabledAt { get; set; }
    /// <summary>最後に明示的な無効化を行った理由。</summary>
    public string? DisabledReason { get; set; }
    /// <summary>最後に明示的な無効化を行った管理者の UserId。管理者削除後も証跡として値を保持する。</summary>
    public int? DisabledByUserId { get; set; }
    /// <summary>最後に明示的な無効化を行った管理者名のスナップショット。</summary>
    public string? DisabledByUsername { get; set; }
    public DateTime PasswordChangedAt { get; set; }
    public DateTime PasswordExpiresAt { get; set; }
    public bool MustChangePassword { get; set; }
    /// <summary>
    /// 初回パスワード設定待ち。true の間は本人がまだパスワードを決めていないため、
    /// 通常ログイン・自動ログインとも一切通さない。この状態のユーザーの
    /// <see cref="PasswordHash"/> には誰も知り得ない使用不能ハッシュが入る
    /// (詳細は Watashi.Server.Services.PasswordSetup)。
    /// </summary>
    public bool IsPasswordSetupPending { get; set; }
    /// <summary>
    /// 初回パスワード設定の受付期限。null は無期限 (既定)。
    /// <see cref="IsPasswordSetupPending"/> が false のときは意味を持たない。
    /// </summary>
    public DateTime? PasswordSetupExpiresAt { get; set; }
    /// <summary>
    /// 初回パスワード設定時に Windows 統合認証で確認できた OS アカウント名 (例: DOMAIN\G012345)。
    /// 本人確認が取れた記録として残す。設定経路が Windows 認証でなかった場合は null。
    /// </summary>
    public string? WindowsAccountName { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>最後にログインした際にクライアントが申告した Windows ユーザー名 (別人ログイン検知用)。</summary>
    public string? LastWindowsUsername { get; set; }
    /// <summary>最後にログインした際にクライアントが申告したマシン名 (端末変更検知用)。</summary>
    public string? LastMachineName { get; set; }
    public DateTime CreatedAt { get; set; }
}
