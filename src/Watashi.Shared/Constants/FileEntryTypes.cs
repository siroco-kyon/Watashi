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
}

public static class SettingKeys
{
    public const string PasswordExpiryDays = "PasswordExpiryDays";
    public const string PasswordWarningDays = "PasswordWarningDays";
    public const string AgentMaxConcurrency = "AgentMaxConcurrency";
    public const string SessionIdleMinutes = "SessionIdleMinutes";
    public const string AuditLogRetentionDays = "AuditLogRetentionDays";
}
