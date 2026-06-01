namespace Watashi.Shared.Constants;

public static class NodeTypes
{
    public const string Direct = "Direct";
    public const string Agent = "Agent";
}

public static class HealthStatuses
{
    public const string Healthy = "Healthy";
    public const string Unhealthy = "Unhealthy";
    public const string Unknown = "Unknown";
}

public static class AuditResults
{
    public const string Success = "success";
    public const string Failure = "failure";
    /// <summary>正常でも失敗でもない注意イベント (別人ログイン・端末変更など)。</summary>
    public const string Warning = "warning";
}
