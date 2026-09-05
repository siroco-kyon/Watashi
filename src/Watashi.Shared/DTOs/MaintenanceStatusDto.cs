namespace Watashi.Shared.DTOs;

public static class MaintenanceStates
{
    public const string Normal = "normal";
    public const string Scheduled = "scheduled";
    public const string Maintenance = "maintenance";
    public const string Recovering = "recovering";

    public static bool IsKnown(string? state) => state is Normal or Scheduled or Maintenance or Recovering;
    public static bool IsBlocking(string? state) => state is Maintenance or Recovering;
}

public sealed record MaintenanceStatusDto
{
    public string State { get; init; } = MaintenanceStates.Normal;
    public long Revision { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime? StartsAtUtc { get; init; }
    public DateTime? ExpectedEndAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public bool IsBlocking => MaintenanceStates.IsBlocking(State);
}

public sealed class MaintenanceUpdateRequest
{
    public long ExpectedRevision { get; set; }
    public string State { get; set; } = MaintenanceStates.Normal;
    public string Message { get; set; } = string.Empty;
    public DateTime? StartsAtUtc { get; set; }
    public DateTime? ExpectedEndAtUtc { get; set; }
}

public sealed class MaintenanceAdminStatusDto
{
    public MaintenanceStatusDto Status { get; init; } = new();
    public int ActiveRequests { get; init; }
    public long? PublishedRevision { get; init; }
    public string? PublicationError { get; init; }
    public bool IsConfigured { get; init; }
}
