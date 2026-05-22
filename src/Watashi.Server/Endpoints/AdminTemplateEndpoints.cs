using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminTemplateEndpoints
{
    public static IEndpointRouteBuilder MapAdminTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/permission-templates").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.PermissionTemplates.AsNoTracking()
                .Select(t => new PermissionTemplateDto
                {
                    Id = t.Id, Name = t.Name,
                    CanRead = t.CanRead, CanWrite = t.CanWrite,
                    CanDelete = t.CanDelete, CanRename = t.CanRename,
                })
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/", async (PermissionTemplateDto dto, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name))
                return Results.BadRequest(new { error = "Name は必須です。" });
            var entity = new PermissionTemplate
            {
                Name = dto.Name,
                CanRead = dto.CanRead, CanWrite = dto.CanWrite,
                CanDelete = dto.CanDelete, CanRename = dto.CanRename,
            };
            db.PermissionTemplates.Add(entity);
            await db.SaveChangesAsync(ct);
            dto.Id = entity.Id;
            await audit.LogAdminAsync(principal, ctx, AdminOperations.TemplateCreate, $"template:{entity.Id}", ct: ct);
            return Results.Created($"/api/admin/permission-templates/{entity.Id}", dto);
        });

        group.MapPatch("/{id:int}", async (int id, PermissionTemplateDto dto, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var entity = await db.PermissionTemplates.FindAsync(new object?[] { id }, ct);
            if (entity is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(dto.Name)) entity.Name = dto.Name;
            entity.CanRead = dto.CanRead;
            entity.CanWrite = dto.CanWrite;
            entity.CanDelete = dto.CanDelete;
            entity.CanRename = dto.CanRename;
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.TemplateUpdate, $"template:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var entity = await db.PermissionTemplates.FindAsync(new object?[] { id }, ct);
            if (entity is null) return Results.NotFound();
            var inUse = await db.UserPermissions.AsNoTracking().AnyAsync(p => p.TemplateId == id, ct);
            if (inUse) return Results.BadRequest(new { error = "テンプレートは使用中のため削除できません。" });
            db.PermissionTemplates.Remove(entity);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.TemplateDelete, $"template:{id}", ct: ct);
            return Results.NoContent();
        });

        return app;
    }
}
