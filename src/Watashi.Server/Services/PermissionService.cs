using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.DTOs;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

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
        var entries = await GetEntriesForShareAsync(userId, shareId, ct);

        foreach (var e in entries)
        {
            if (!PathHelper.IsPathWithin(e.AllowedPath, normalized)) continue;
            bool allowed = operation switch
            {
                Shared.Constants.Operations.Read => e.CanRead,
                Shared.Constants.Operations.Write => e.CanWrite,
                Shared.Constants.Operations.Delete => e.CanDelete,
                Shared.Constants.Operations.Rename => e.CanRename,
                _ => false,
            };
            if (allowed) return (true, e.Id);
        }
        return (false, null);
    }

    public async Task<bool> IsPermissionRootAsync(int userId, int shareId, string path, CancellationToken ct = default)
    {
        var n = PathHelper.NormalizePath(path);
        var entries = await GetEntriesForShareAsync(userId, shareId, ct);
        // SMB は大文字小文字を区別しないため、CanPerformAsync (IsPathWithin) と同じく
        // 大文字小文字を無視して比較しないと、表記違いで許可ルート保護をすり抜けられる。
        return entries.Any(e => string.Equals(
            PathHelper.NormalizePath(e.AllowedPath), n, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<LocationDto>> GetUserLocationsAsync(int userId, int? hostId = null, int? shareId = null, CancellationToken ct = default)
    {
        var query =
            from p in _db.UserPermissions.AsNoTracking()
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
                    Read = t.CanRead, Write = t.CanWrite, Delete = t.CanDelete, Rename = t.CanRename,
                },
            };
        return await query.ToListAsync(ct);
    }

    private async Task<List<PermissionEntry>> GetEntriesForShareAsync(int userId, int shareId, CancellationToken ct)
    {
        var cacheKey = $"perm:{userId}:{shareId}";
        var items = _httpCtx?.HttpContext?.Items;
        if (items is not null && items.TryGetValue(cacheKey, out var cached) && cached is List<PermissionEntry> hit)
            return hit;

        var entries = await _db.UserPermissions.AsNoTracking()
            .Where(p => p.UserId == userId && p.ShareId == shareId)
            .Join(_db.PermissionTemplates.AsNoTracking(), p => p.TemplateId, t => t.Id,
                (p, t) => new PermissionEntry(p.Id, p.AllowedPath, t.CanRead, t.CanWrite, t.CanDelete, t.CanRename))
            .ToListAsync(ct);

        if (items is not null) items[cacheKey] = entries;
        return entries;
    }

    private record PermissionEntry(int Id, string AllowedPath, bool CanRead, bool CanWrite, bool CanDelete, bool CanRename);
}
