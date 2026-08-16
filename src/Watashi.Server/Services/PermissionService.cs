using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.DTOs;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Services;

public class PermissionService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor? _httpCtx;

    public PermissionService(AppDbContext db, IHttpContextAccessor? httpCtx = null)
    {
        _db = db;
        _httpCtx = httpCtx;
    }

    public async Task<(bool allowed, int? permissionId)> CanPerformAsync(
        int userId, int shareId, string path, string operation, CancellationToken ct = default)
    {
        var normalized = PathHelper.NormalizePath(path);
        var entries = await GetActiveEntriesForShareAsync(userId, shareId, DateTime.UtcNow, ct);
        var grant = FindGrant(entries, normalized, operation);
        return grant is null ? (false, null) : (true, grant.Id);
    }

    public async Task<bool> IsPermissionRootAsync(int userId, int shareId, string path, CancellationToken ct = default)
    {
        var n = PathHelper.NormalizePath(path);
        var entries = await GetActiveEntriesForShareAsync(userId, shareId, DateTime.UtcNow, ct);
        // SMB は大文字小文字を区別しないため、CanPerformAsync (IsPathWithin) と同じく
        // 大文字小文字を無視して比較しないと、表記違いで許可ルート保護をすり抜けられる。
        return entries.Any(e => string.Equals(
            PathHelper.NormalizePath(e.AllowedPath), n, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<LocationDto>> GetUserLocationsAsync(int userId, int? hostId = null, int? shareId = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var activePermissions = _db.UserPermissions.AsNoTracking()
            .Where(UserPermissionRules.ActiveAt(now));
        var query =
            from p in activePermissions
            join s in _db.CifsShares on p.ShareId equals s.Id
            join h in _db.CifsHosts on s.HostId equals h.Id
            join t in _db.PermissionTemplates on p.TemplateId equals t.Id
            where p.UserId == userId
                && (hostId == null || h.Id == hostId)
                && (shareId == null || s.Id == shareId)
            select new LocationDto
            {
                PermissionId = p.Id,
                HostId = h.Id,
                ShareId = s.Id,
                Path = p.AllowedPath,
                DisplayName = string.IsNullOrWhiteSpace(p.DisplayName)
                    ? h.Name + " / " + s.DisplayName + " / " + p.AllowedPath
                    : p.DisplayName,
                HostName = h.Name,
                ShareName = s.DisplayName,
                Permissions = new LocationPermissions
                {
                    Read = t.CanRead,
                    Write = t.CanWrite,
                    Delete = t.CanDelete,
                    Rename = t.CanRename,
                },
            };
        return await query.ToListAsync(ct);
    }

    /// <summary>
    /// 指定時点の実効権限を、実際の認可と同じパス正規化・有効期間・操作判定で説明する。
    /// DB は一切更新しない。
    /// </summary>
    public async Task<PermissionSimulationResponse> SimulateAsync(
        int userId,
        int shareId,
        string path,
        DateTime? evaluatedAt = null,
        CancellationToken ct = default)
    {
        var at = (evaluatedAt ?? DateTime.UtcNow).ToUniversalTime();
        var normalized = PathHelper.NormalizePath(path);
        var allEntries = await GetAllEntriesForShareAsync(userId, shareId, ct);
        var matchingActive = allEntries
            .Where(e => UserPermissionRules.IsActive(e.ValidFrom, e.ExpiresAt, at) &&
                        PathHelper.IsPathWithin(e.AllowedPath, normalized))
            .OrderByDescending(e => PathHelper.NormalizePath(e.AllowedPath).Length)
            .ThenBy(e => e.Id)
            .ToList();

        var warningMap = UserPermissionRules.Analyze(allEntries.Select(ToGrant), at);
        var relevantWarnings = matchingActive
            .SelectMany(e => warningMap.TryGetValue(e.Id, out var warnings)
                ? warnings
                : Enumerable.Empty<PermissionWarningDto>())
            .GroupBy(w => new { w.Code, w.Message })
            .Select(g => new PermissionWarningDto
            {
                Code = g.Key.Code,
                Message = g.Key.Message,
                RelatedPermissionIds = g.SelectMany(x => x.RelatedPermissionIds).Distinct().OrderBy(x => x).ToList(),
            })
            .ToList();

        return new PermissionSimulationResponse
        {
            UserId = userId,
            ShareId = shareId,
            NormalizedPath = normalized,
            EvaluatedAt = at,
            Read = BuildDecision(matchingActive, normalized, Shared.Constants.Operations.Read),
            Write = BuildDecision(matchingActive, normalized, Shared.Constants.Operations.Write),
            Delete = BuildDecision(matchingActive, normalized, Shared.Constants.Operations.Delete),
            Rename = BuildDecision(matchingActive, normalized, Shared.Constants.Operations.Rename),
            Warnings = relevantWarnings,
        };
    }

    private async Task<List<PermissionEntry>> GetActiveEntriesForShareAsync(
        int userId,
        int shareId,
        DateTime utcNow,
        CancellationToken ct)
    {
        var cacheKey = $"perm:{userId}:{shareId}";
        var items = _httpCtx?.HttpContext?.Items;
        if (items is not null && items.TryGetValue(cacheKey, out var cached) && cached is List<PermissionEntry> hit)
            return hit;

        var active = _db.UserPermissions.AsNoTracking().Where(UserPermissionRules.ActiveAt(utcNow));
        var entries = await (
            from p in active
            join t in _db.PermissionTemplates.AsNoTracking() on p.TemplateId equals t.Id
            where p.UserId == userId && p.ShareId == shareId
            select new PermissionEntry(
                p.Id, p.UserId, p.ShareId, p.TemplateId, t.Name, p.AllowedPath,
                p.ValidFrom, p.ExpiresAt, t.CanRead, t.CanWrite, t.CanDelete, t.CanRename))
            .ToListAsync(ct);

        entries = entries
            .OrderByDescending(e => PathHelper.NormalizePath(e.AllowedPath).Length)
            .ThenBy(e => e.Id)
            .ToList();

        if (items is not null) items[cacheKey] = entries;
        return entries;
    }

    private async Task<List<PermissionEntry>> GetAllEntriesForShareAsync(
        int userId,
        int shareId,
        CancellationToken ct)
        => await (
            from p in _db.UserPermissions.AsNoTracking()
            join t in _db.PermissionTemplates.AsNoTracking() on p.TemplateId equals t.Id
            where p.UserId == userId && p.ShareId == shareId
            select new PermissionEntry(
                p.Id, p.UserId, p.ShareId, p.TemplateId, t.Name, p.AllowedPath,
                p.ValidFrom, p.ExpiresAt, t.CanRead, t.CanWrite, t.CanDelete, t.CanRename))
            .ToListAsync(ct);

    private static PermissionEntry? FindGrant(
        IEnumerable<PermissionEntry> entries,
        string normalizedPath,
        string operation)
        => entries.FirstOrDefault(e =>
            PathHelper.IsPathWithin(e.AllowedPath, normalizedPath) && Allows(e, operation));

    private static bool Allows(PermissionEntry entry, string operation) => operation switch
    {
        Shared.Constants.Operations.Read => entry.CanRead,
        Shared.Constants.Operations.Write => entry.CanWrite,
        Shared.Constants.Operations.Delete => entry.CanDelete,
        Shared.Constants.Operations.Rename => entry.CanRename,
        _ => false,
    };

    private static PermissionSimulationDecision BuildDecision(
        IReadOnlyCollection<PermissionEntry> matchingActive,
        string normalizedPath,
        string operation)
    {
        var grant = FindGrant(matchingActive, normalizedPath, operation);
        if (grant is not null)
        {
            return new PermissionSimulationDecision
            {
                Operation = operation,
                Allowed = true,
                PermissionId = grant.Id,
                MatchedAllowedPath = PathHelper.NormalizePath(grant.AllowedPath),
                TemplateId = grant.TemplateId,
                TemplateName = grant.TemplateName,
                MatchRule = "active_allowed_path_prefix",
                Explanation = $"有効な権限 #{grant.Id} の許可パスに含まれ、テンプレートが {operation.ToUpperInvariant()} を許可しています。",
            };
        }

        var hasScopeMatch = matchingActive.Count > 0;
        return new PermissionSimulationDecision
        {
            Operation = operation,
            Allowed = false,
            MatchRule = hasScopeMatch ? "scope_matched_but_operation_not_granted" : "no_active_path_match",
            Explanation = hasScopeMatch
                ? $"パス範囲に一致する有効な権限はありますが、{operation.ToUpperInvariant()} を許可するテンプレートがありません。"
                : "このパスを含む有効期間内の権限がありません。",
        };
    }

    private static UserPermissionRules.Grant ToGrant(PermissionEntry e)
        => new(e.Id, e.UserId, e.ShareId, e.TemplateId, e.TemplateName, e.AllowedPath,
            e.ValidFrom, e.ExpiresAt, e.CanRead, e.CanWrite, e.CanDelete, e.CanRename);

    private sealed record PermissionEntry(
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
}
