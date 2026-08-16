using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.DTOs;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Endpoints;

public static class HostEndpoints
{
    public static IEndpointRouteBuilder MapHostEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hosts").RequireAuthorization();

        group.MapGet("/", async (AppDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            var activePermissions = db.UserPermissions.AsNoTracking()
                .Where(UserPermissionRules.ActiveAt(DateTime.UtcNow));

            var hosts = await (
                from p in activePermissions
                join s in db.CifsShares on p.ShareId equals s.Id
                join h in db.CifsHosts on s.HostId equals h.Id
                where p.UserId == userId
                select new { h.Id, h.Name, h.Description }
            ).Distinct().ToListAsync(ct);
            return Results.Ok(hosts);
        });

        group.MapGet("/{hostId:int}/shares", async (int hostId, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            var activePermissions = db.UserPermissions.AsNoTracking()
                .Where(UserPermissionRules.ActiveAt(DateTime.UtcNow));

            var shares = await (
                from p in activePermissions
                join s in db.CifsShares on p.ShareId equals s.Id
                where p.UserId == userId && s.HostId == hostId
                select new { s.Id, s.ShareName, s.DisplayName }
            ).Distinct().ToListAsync(ct);
            return Results.Ok(shares);
        });

        group.MapGet("/{hostId:int}/shares/{shareId:int}/locations", async (
            int hostId, int shareId, PermissionService perms, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            var list = await perms.GetUserLocationsAsync(userId, hostId, shareId, ct);
            return Results.Ok(list);
        });

        // ユーザがアクセス可能な全 host/share/location を 1 リクエストで返す集約エンドポイント。
        // クライアントの N×M HTTP ループを排除するために追加。
        group.MapGet("/catalog", async (AppDbContext db, PermissionService perms, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();

            var allLocations = await perms.GetUserLocationsAsync(userId, ct: ct);
            var activePermissions = db.UserPermissions.AsNoTracking()
                .Where(UserPermissionRules.ActiveAt(DateTime.UtcNow));
            var hostMeta = await (
                from p in activePermissions
                join s in db.CifsShares on p.ShareId equals s.Id
                join h in db.CifsHosts on s.HostId equals h.Id
                where p.UserId == userId
                select new { h.Id, h.Name, h.Description, ShareId = s.Id, s.ShareName, ShareDisplay = s.DisplayName }
            ).Distinct().ToListAsync(ct);

            var hosts = hostMeta
                .GroupBy(x => new { x.Id, x.Name, x.Description })
                .Select(g => new
                {
                    id = g.Key.Id,
                    name = g.Key.Name,
                    description = g.Key.Description,
                    shares = g.GroupBy(s => new { s.ShareId, s.ShareName, s.ShareDisplay })
                        .Select(sg => new
                        {
                            id = sg.Key.ShareId,
                            shareName = sg.Key.ShareName,
                            displayName = sg.Key.ShareDisplay,
                            locations = allLocations
                                .Where(l => l.HostId == g.Key.Id && l.ShareId == sg.Key.ShareId)
                                .ToList(),
                        }).ToList(),
                }).ToList();

            return Results.Ok(new { hosts });
        });

        return app;
    }
}
