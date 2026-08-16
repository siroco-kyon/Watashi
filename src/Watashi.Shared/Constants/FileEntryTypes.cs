namespace Watashi.Shared.Constants;

public static class FileEntryTypes
{
    public const string File = "file";
    public const string Directory = "directory";
    public const string Parent = "parent";
}

public static class AuthClaims
{
    public const string UserId = "uid";
    public const string Role = "role";
    public const string Admin = "Admin";
    public const string User = "User";
    /// <summary>
    /// access token 発行時点の <c>User.PasswordChangedAt</c> (UTC ticks)。
    /// DB 上の値と一致しないトークンは、パスワード変更・管理者リセット・初回設定待ちへの
    /// 変更より前に発行されたものとして認証時に拒否する。
    /// </summary>
    public const string CredentialVersion = "cv";
    /// <summary>
    /// access token に付与される「パスワード変更必須」フラグ。値は "1" 固定。
    /// 付与されたトークンは <c>/api/auth/change-password</c> / <c>/api/auth/logout</c> /
    /// <c>/api/auth/refresh</c> 以外のエンドポイントで 403 にブロックされる。
    /// </summary>
    public const string MustChangePassword = "mcp";
}

public static class SettingKeys
{
    public const string PasswordExpiryDays = "PasswordExpiryDays";
    public const string PasswordWarningDays = "PasswordWarningDays";
    public const string AgentMaxConcurrency = "AgentMaxConcurrency";
    public const string SessionIdleMinutes = "SessionIdleMinutes";
    public const string AuditLogRetentionDays = "AuditLogRetentionDays";
    public const string MaxFailedLoginAttempts = "MaxFailedLoginAttempts";
    /// <summary>初回パスワード設定を受け付ける日数。0 は無期限 (既定)。</summary>
    public const string PasswordSetupExpiryDays = "PasswordSetupExpiryDays";
    /// <summary>リモートごみ箱の保管日数。</summary>
    public const string TrashRetentionDays = "TrashRetentionDays";
    /// <summary>共有ごとのリモートごみ箱容量上限 (bytes)。0 は無制限。</summary>
    public const string TrashCapacityBytes = "TrashCapacityBytes";
    /// <summary>1ユーザーが同時に保持できる有効な信頼端末数。</summary>
    public const string TrustedDeviceLimit = "TrustedDeviceLimit";
}
