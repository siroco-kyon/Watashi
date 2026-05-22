using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;

namespace Watashi.Server.Endpoints;

public static class HostEndpoints
{
    public static IEndpointRouteBuilder MapHostEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hosts").RequireAuthorization();

        group.MapGet("/", async (
            AppDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!int.TryParse(principal.FindFirst("uid")?.Value, out var userId))
                return Results.Unauthorized();

            var hosts = await (
                from p in db.UserPermissions
                join s in db.CifsShares on p.ShareId equals s.Id
                join h in db.CifsHosts on s.HostId equals h.Id
                where p.UserId == userId
                select new { h.Id, h.Name, h.Description }
            ).Distinct().ToListAsync(ct);
            return Results.Ok(hosts);
        });

        group.MapGet("/{hostId:int}/shares", async (
            int hostId,
            AppDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!int.TryParse(principal.FindFirst("uid")?.Value, out var userId))
                return Results.Unauthorized();

            var shares = await (
                from p in db.UserPermissions
                join s in db.CifsShares on p.ShareId equals s.Id
                where p.UserId == userId && s.HostId == hostId
                select new { s.Id, s.ShareName, s.DisplayName }
            ).Distinct().ToListAsync(ct);
            return Results.Ok(shares);
        });

        group.MapGet("/{hostId:int}/shares/{shareId:int}/locations", async (
            int hostId,
            int shareId,
            PermissionService perms,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!int.TryParse(principal.FindFirst("uid")?.Value, out var userId))
                return Results.Unauthorized();
            var list = await perms.GetUserLocationsAsync(userId, hostId, shareId, ct);
            return Results.Ok(list);
        });

        return app;
    }
}
