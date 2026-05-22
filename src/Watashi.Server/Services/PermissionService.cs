using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.DTOs;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Services;

public class PermissionService
{
    private readonly AppDbContext _db;

    public PermissionService(AppDbContext db) => _db = db;

    public async Task<(bool allowed, int? permissionId)> CanPerformAsync(
        int userId, int shareId, string path, string operation, CancellationToken ct = default)
    {
        var normalized = PathHelper.NormalizePath(path);
        var entries = await _db.UserPermissions
            .Include(p => p.Template)
            .Where(p => p.UserId == userId && p.ShareId == shareId)
            .ToListAsync(ct);

        foreach (var e in entries)
        {
            if (e.Template is null) continue;
            if (!PathHelper.IsPathWithin(e.AllowedPath, normalized)) continue;
            bool allowed = operation switch
            {
                Shared.Constants.Operations.Read => e.Template.CanRead,
                Shared.Constants.Operations.Write => e.Template.CanWrite,
                Shared.Constants.Operations.Delete => e.Template.CanDelete,
                Shared.Constants.Operations.Rename => e.Template.CanRename,
                _ => false,
            };
            if (allowed) return (true, e.Id);
        }
        return (false, null);
    }

    public async Task<bool> IsPermissionRootAsync(int userId, int shareId, string path, CancellationToken ct = default)
    {
        var n = PathHelper.NormalizePath(path);
        return await _db.UserPermissions
            .AnyAsync(p => p.UserId == userId && p.ShareId == shareId && p.AllowedPath == n, ct);
    }

    public async Task<List<LocationDto>> GetUserLocationsAsync(int userId, int? hostId = null, int? shareId = null, CancellationToken ct = default)
    {
        var query =
            from p in _db.UserPermissions
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
                DisplayName = p.DisplayName,
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
}
