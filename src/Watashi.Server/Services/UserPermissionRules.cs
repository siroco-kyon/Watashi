using System.Linq.Expressions;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

/// <summary>期限付きユーザー権限の有効判定、入力正規化、構成上の警告を一箇所に集約する。</summary>
public static class UserPermissionRules
{
    public sealed record NormalizedMetadata(
        DateTime? ValidFrom, DateTime? ExpiresAt, string? Reason, string? TicketNumber);

    public sealed record Grant(
        int Id,
        int UserId,
        int ShareId,
        int TemplateId,
        string? TemplateName,
        string AllowedPath,
        DateTime? ValidFrom,
        DateTime? ExpiresAt,
        bool CanRead,
        bool CanWrite,
        bool CanDelete,
        bool CanRename);

    /// <summary>ExpiresAt は排他的境界。既存の null/null 権限は常に有効。</summary>
    public static bool IsActive(DateTime? validFrom, DateTime? expiresAt, DateTime utcNow)
        => (!validFrom.HasValue || validFrom.Value <= utcNow)
           && (!expiresAt.HasValue || expiresAt.Value > utcNow);

    public static Expression<Func<UserPermission, bool>> ActiveAt(DateTime utcNow)
        => p => (!p.ValidFrom.HasValue || p.ValidFrom.Value <= utcNow)
                && (!p.ExpiresAt.HasValue || p.ExpiresAt.Value > utcNow);

    public static string GetEffectiveStatus(DateTime? validFrom, DateTime? expiresAt, DateTime utcNow)
        => validFrom.HasValue && validFrom.Value > utcNow
            ? PermissionEffectiveStatuses.Scheduled
            : expiresAt.HasValue && expiresAt.Value <= utcNow
                ? PermissionEffectiveStatuses.Expired
                : PermissionEffectiveStatuses.Active;

    public static bool TryNormalizeMetadata(
        DateTime? validFrom,
        DateTime? expiresAt,
        string? reason,
        string? ticketNumber,
        out NormalizedMetadata normalized,
        out string? error)
    {
        normalized = new NormalizedMetadata(null, null, null, null);
        error = null;

        if (!TryUtc(validFrom, "有効開始日時", out var fromUtc, out error) ||
            !TryUtc(expiresAt, "有効期限", out var expiresUtc, out error))
            return false;

        if (fromUtc.HasValue && expiresUtc.HasValue && fromUtc.Value >= expiresUtc.Value)
        {
            error = "有効期限は有効開始日時より後にしてください。";
            return false;
        }

        var normalizedReason = NullIfWhiteSpace(reason);
        if (normalizedReason?.Length > CreateUserPermissionRequest.MaxReasonLength)
        {
            error = $"理由は {CreateUserPermissionRequest.MaxReasonLength} 文字以内で入力してください。";
            return false;
        }

        var normalizedTicket = NullIfWhiteSpace(ticketNumber);
        if (normalizedTicket?.Length > CreateUserPermissionRequest.MaxTicketNumberLength)
        {
            error = $"チケット/申請番号は {CreateUserPermissionRequest.MaxTicketNumberLength} 文字以内で入力してください。";
            return false;
        }

        normalized = new NormalizedMetadata(fromUtc, expiresUtc, normalizedReason, normalizedTicket);
        return true;
    }

    /// <summary>
    /// 現在または将来に効力を持ち得る権限について、重複・冗長・共有全体への広域付与を警告する。
    /// 警告は操作を拒否せず、管理者が意図を再確認するための材料として返す。
    /// </summary>
    public static IReadOnlyDictionary<int, List<PermissionWarningDto>> Analyze(
        IEnumerable<Grant> source,
        DateTime utcNow)
    {
        var grants = source
            .Where(g => !g.ExpiresAt.HasValue || g.ExpiresAt.Value > utcNow)
            .Select(g => g with { AllowedPath = PathHelper.NormalizePath(g.AllowedPath) })
            .ToList();
        var result = grants.ToDictionary(g => g.Id, _ => new List<PermissionWarningDto>());

        foreach (var grant in grants.Where(g => g.AllowedPath == "/"))
        {
            result[grant.Id].Add(new PermissionWarningDto
            {
                Code = PermissionWarningCodes.BroadScope,
                Message = "共有全体 (/) への広域権限です。必要最小限のパスか確認してください。",
            });
        }

        for (var i = 0; i < grants.Count; i++)
        {
            for (var j = i + 1; j < grants.Count; j++)
            {
                var left = grants[i];
                var right = grants[j];
                if (left.UserId != right.UserId || left.ShareId != right.ShareId ||
                    !string.Equals(left.AllowedPath, right.AllowedPath, StringComparison.OrdinalIgnoreCase) ||
                    !WindowsOverlap(left, right))
                    continue;

                AddRelatedWarning(result[left.Id], PermissionWarningCodes.DuplicateScope,
                    "同じパス・重なる有効期間の権限があります。意図した複数付与か確認してください。", right.Id);
                AddRelatedWarning(result[right.Id], PermissionWarningCodes.DuplicateScope,
                    "同じパス・重なる有効期間の権限があります。意図した複数付与か確認してください。", left.Id);
            }
        }

        foreach (var grant in grants)
        {
            var covering = grants
                .Where(other => other.Id != grant.Id &&
                                other.UserId == grant.UserId &&
                                other.ShareId == grant.ShareId &&
                                WindowCovers(other, grant) &&
                                CapabilitiesCover(other, grant) &&
                                PathHelper.IsPathWithin(other.AllowedPath, grant.AllowedPath) &&
                                (!string.Equals(other.AllowedPath, grant.AllowedPath, StringComparison.OrdinalIgnoreCase) ||
                                 HasStrictlyMoreCapabilities(other, grant)))
                .OrderByDescending(other => PathHelper.NormalizePath(other.AllowedPath).Length)
                .ThenBy(other => other.Id)
                .FirstOrDefault();
            if (covering is null) continue;

            result[grant.Id].Add(new PermissionWarningDto
            {
                Code = PermissionWarningCodes.RedundantGrant,
                Message = $"権限 #{covering.Id} の範囲・期間・操作に包含されるため冗長です。",
                RelatedPermissionIds = new List<int> { covering.Id },
            });
        }

        return result;
    }

    private static bool TryUtc(
        DateTime? value,
        string fieldName,
        out DateTime? utc,
        out string? error)
    {
        utc = null;
        error = null;
        if (!value.HasValue) return true;
        if (value.Value.Kind == DateTimeKind.Unspecified)
        {
            error = $"{fieldName}は UTC (末尾 Z) で指定してください。";
            return false;
        }
        utc = value.Value.ToUniversalTime();
        return true;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool WindowsOverlap(Grant a, Grant b)
        => (!a.ExpiresAt.HasValue || !b.ValidFrom.HasValue || b.ValidFrom.Value < a.ExpiresAt.Value)
           && (!b.ExpiresAt.HasValue || !a.ValidFrom.HasValue || a.ValidFrom.Value < b.ExpiresAt.Value);

    private static bool WindowCovers(Grant outer, Grant inner)
    {
        var startsBefore = !outer.ValidFrom.HasValue ||
                           (inner.ValidFrom.HasValue && outer.ValidFrom.Value <= inner.ValidFrom.Value);
        var endsAfter = !outer.ExpiresAt.HasValue ||
                        (inner.ExpiresAt.HasValue && outer.ExpiresAt.Value >= inner.ExpiresAt.Value);
        return startsBefore && endsAfter;
    }

    private static bool CapabilitiesCover(Grant outer, Grant inner)
        => (!inner.CanRead || outer.CanRead)
           && (!inner.CanWrite || outer.CanWrite)
           && (!inner.CanDelete || outer.CanDelete)
           && (!inner.CanRename || outer.CanRename);

    private static bool HasStrictlyMoreCapabilities(Grant outer, Grant inner)
        => (outer.CanRead && !inner.CanRead)
           || (outer.CanWrite && !inner.CanWrite)
           || (outer.CanDelete && !inner.CanDelete)
           || (outer.CanRename && !inner.CanRename);

    private static void AddRelatedWarning(
        ICollection<PermissionWarningDto> warnings,
        string code,
        string message,
        int relatedId)
    {
        var existing = warnings.FirstOrDefault(w => w.Code == code);
        if (existing is null)
        {
            existing = new PermissionWarningDto { Code = code, Message = message };
            warnings.Add(existing);
        }
        if (!existing.RelatedPermissionIds.Contains(relatedId))
            existing.RelatedPermissionIds.Add(relatedId);
    }
}
