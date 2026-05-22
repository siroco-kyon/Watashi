using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminShareEndpoints
{
    public static IEndpointRouteBuilder MapAdminShareEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/shares").RequireAuthorization("Admin");

        group.MapGet("/", async (int? hostId, AppDbContext db, CancellationToken ct) =>
        {
            var query = from s in db.CifsShares.AsNoTracking()
                join h in db.CifsHosts on s.HostId equals h.Id
                where hostId == null || s.HostId == hostId
                select new ShareDto { Id = s.Id, HostId = h.Id, HostName = h.Name, ShareName = s.ShareName, DisplayName = s.DisplayName };
            return Results.Ok(await query.ToListAsync(ct));
        });

        group.MapPost("/", async (CreateShareRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ShareName)) return Results.BadRequest(new { error = "ShareName 必須" });
            if (!await db.CifsHosts.AsNoTracking().AnyAsync(h => h.Id == req.HostId, ct)) return Results.BadRequest(new { error = "Host 不在" });
            var s = new CifsShare { HostId = req.HostId, ShareName = req.ShareName, DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? req.ShareName : req.DisplayName };
            db.CifsShares.Add(s);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareCreate, $"share:{s.Id}", ct: ct);
            return Results.Created($"/api/admin/shares/{s.Id}", new { id = s.Id });
        });

        group.MapPatch("/{id:int}", async (int id, UpdateShareRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var s = await db.CifsShares.FindAsync(new object?[] { id }, ct);
            if (s is null) return Results.NotFound();
            if (req.DisplayName is not null) s.DisplayName = req.DisplayName;
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareUpdate, $"share:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var s = await db.CifsShares.FindAsync(new object?[] { id }, ct);
            if (s is null) return Results.NotFound();
            db.CifsShares.Remove(s);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareDelete, $"share:{id}", ct: ct);
            return Results.NoContent();
        });

        return app;
    }
}
