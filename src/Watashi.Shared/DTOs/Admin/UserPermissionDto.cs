namespace Watashi.Shared.DTOs.Admin;

public class UserPermissionDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string? Username { get; set; }
    public int ShareId { get; set; }
    public string? ShareName { get; set; }
    public string? HostName { get; set; }
    public int TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Reason { get; set; }
    public string? TicketNumber { get; set; }
    public string EffectiveStatus { get; set; } = PermissionEffectiveStatuses.Active;
    public List<PermissionWarningDto> Warnings { get; set; } = new();
    public DateTime CreatedAt { get; set; }

    public string EffectiveStatusLabel => EffectiveStatus switch
    {
        PermissionEffectiveStatuses.Scheduled => "開始前",
        PermissionEffectiveStatuses.Expired => "期限切れ",
        _ => "有効",
    };

    public string WarningSummary => string.Join(" / ", Warnings.Select(w => w.Message).Distinct());
    public string WarningLabel => Warnings.Count == 0 ? string.Empty : $"⚠ {Warnings.Count}";
}

public class CreateUserPermissionRequest
{
    public const int MaxReasonLength = 500;
    public const int MaxTicketNumberLength = 100;

    public int UserId { get; set; }
    public int ShareId { get; set; }
    public int TemplateId { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Reason { get; set; }
    public string? TicketNumber { get; set; }
}

public class UpdateUserPermissionRequest
{
    public int ShareId { get; set; }
    public int TemplateId { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Reason { get; set; }
    public string? TicketNumber { get; set; }
}

public static class PermissionEffectiveStatuses
{
    public const string Active = "active";
    public const string Scheduled = "scheduled";
    public const string Expired = "expired";
}

public static class PermissionWarningCodes
{
    public const string DuplicateScope = "duplicate_scope";
    public const string RedundantGrant = "redundant_grant";
    public const string BroadScope = "broad_scope";
}

public class PermissionWarningDto
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<int> RelatedPermissionIds { get; set; } = new();
}

public class UserPermissionMutationResultDto
{
    public int Id { get; set; }
    public List<PermissionWarningDto> Warnings { get; set; } = new();
}

public class PermissionSimulationResponse
{
    public int UserId { get; set; }
    public int ShareId { get; set; }
    public string NormalizedPath { get; set; } = "/";
    public DateTime EvaluatedAt { get; set; }
    public PermissionSimulationDecision Read { get; set; } = new();
    public PermissionSimulationDecision Write { get; set; } = new();
    public PermissionSimulationDecision Delete { get; set; } = new();
    public PermissionSimulationDecision Rename { get; set; } = new();
    public List<PermissionWarningDto> Warnings { get; set; } = new();
}

public class PermissionSimulationDecision
{
    public string Operation { get; set; } = string.Empty;
    public bool Allowed { get; set; }
    public int? PermissionId { get; set; }
    public string? MatchedAllowedPath { get; set; }
    public int? TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public string MatchRule { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;

    public string ResultLabel => Allowed ? "許可" : "拒否";
    public string EvidenceLabel => PermissionId.HasValue
        ? $"Permission #{PermissionId}: {MatchedAllowedPath}"
        : "根拠となる有効な Permission なし";
}
