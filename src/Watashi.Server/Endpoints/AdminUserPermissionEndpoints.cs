using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminUserPermissionEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserPermissionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/user-permissions").RequireAuthorization("Admin");

        group.MapGet("/", async (int? userId, int? shareId, AppDbContext db, CancellationToken ct) =>
        {
            var query =
                from p in db.UserPermissions
                join u in db.Users on p.UserId equals u.Id
                join s in db.CifsShares on p.ShareId equals s.Id
                join h in db.CifsHosts on s.HostId equals h.Id
                join t in db.PermissionTemplates on p.TemplateId equals t.Id
                where (userId == null || p.UserId == userId)
                    && (shareId == null || p.ShareId == shareId)
                select new UserPermissionDto
                {
                    Id = p.Id,
                    UserId = u.Id,
                    Username = u.Username,
                    ShareId = s.Id,
                    ShareName = s.DisplayName,
                    HostName = h.Name,
                    TemplateId = t.Id,
                    TemplateName = t.Name,
                    AllowedPath = p.AllowedPath,
                    DisplayName = p.DisplayName,
                    CreatedAt = p.CreatedAt,
                };
            var items = await query.ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/", async (
            CreateUserPermissionRequest req,
            AppDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync(new object?[] { req.UserId }, ct);
            if (user is null) return Results.BadRequest(new { error = "User が存在しません。" });
            var share = await db.CifsShares.FindAsync(new object?[] { req.ShareId }, ct);
            if (share is null) return Results.BadRequest(new { error = "Share が存在しません。" });
            var template = await db.PermissionTemplates.FindAsync(new object?[] { req.TemplateId }, ct);
            if (template is null) return Results.BadRequest(new { error = "Template が存在しません。" });

            var normalized = PathHelper.NormalizePath(req.AllowedPath);
            int? createdBy = int.TryParse(principal.FindFirst("uid")?.Value, out var uid) ? uid : null;
            var entity = new UserPermission
            {
                UserId = req.UserId,
                ShareId = req.ShareId,
                TemplateId = req.TemplateId,
                AllowedPath = normalized,
                DisplayName = req.DisplayName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = createdBy,
            };
            db.UserPermissions.Add(entity);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/user-permissions/{entity.Id}", new { id = entity.Id });
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var entity = await db.UserPermissions.FindAsync(new object?[] { id }, ct);
            if (entity is null) return Results.NotFound();
            db.UserPermissions.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}
