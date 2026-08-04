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
}
