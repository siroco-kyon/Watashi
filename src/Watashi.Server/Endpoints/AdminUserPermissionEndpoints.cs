using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
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
                from p in db.UserPermissions.AsNoTracking()
                join u in db.Users on p.UserId equals u.Id
                join s in db.CifsShares on p.ShareId equals s.Id
                join h in db.CifsHosts on s.HostId equals h.Id
                join t in db.PermissionTemplates on p.TemplateId equals t.Id
                where (userId == null || p.UserId == userId)
                    && (shareId == null || p.ShareId == shareId)
                select new UserPermissionDto
                {
                    Id = p.Id, UserId = u.Id, Username = u.Username,
                    ShareId = s.Id, ShareName = s.DisplayName, HostName = h.Name,
                    TemplateId = t.Id, TemplateName = t.Name,
                    AllowedPath = p.AllowedPath, DisplayName = p.DisplayName, CreatedAt = p.CreatedAt,
                };
            var items = await query.ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/", async (
            CreateUserPermissionRequest req,
            AppDbContext db,
            AuditLogService audit,
            HttpContext ctx,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == req.UserId, ct))
                return Results.BadRequest(new { error = "User が存在しません。" });
            if (!await db.CifsShares.AsNoTracking().AnyAsync(s => s.Id == req.ShareId, ct))
                return Results.BadRequest(new { error = "Share が存在しません。" });
            if (!await db.PermissionTemplates.AsNoTracking().AnyAsync(t => t.Id == req.TemplateId, ct))
                return Results.BadRequest(new { error = "Template が存在しません。" });

            var normalized = PathHelper.NormalizePath(req.AllowedPath);
            int? createdBy = principal.GetUserId();
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
            await audit.LogAdminAsync(principal, ctx, AdminOperations.PermissionCreate,
                $"perm:user={req.UserId},share={req.ShareId},path={normalized}", ct: ct);
            return Results.Created($"/api/admin/user-permissions/{entity.Id}", new { id = entity.Id });
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var entity = await db.UserPermissions.FindAsync(new object?[] { id }, ct);
            if (entity is null) return Results.NotFound();
            db.UserPermissions.Remove(entity);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.PermissionDelete, $"perm:{id}", ct: ct);
            return Results.NoContent();
        });

        // ===== 他ユーザーから権限をコピー =====
        // 全件 (PermissionIds 未指定) または指定 ID 群のみコピー。
        // 既存の (Share, AllowedPath) と衝突した場合: Overwrite=true でテンプレ等更新、false でスキップ。
        group.MapPost("/copy", async (CopyUserPermissionsRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (req.FromUserId == req.ToUserId)
                return Results.BadRequest(new { error = "コピー元とコピー先が同じユーザーです。" });
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == req.FromUserId, ct))
                return Results.BadRequest(new { error = "コピー元ユーザーが存在しません。" });
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == req.ToUserId, ct))
                return Results.BadRequest(new { error = "コピー先ユーザーが存在しません。" });

            var srcQuery = db.UserPermissions.AsNoTracking()
                .Where(p => p.UserId == req.FromUserId);
            if (req.PermissionIds is { Count: > 0 })
                srcQuery = srcQuery.Where(p => req.PermissionIds.Contains(p.Id));
            var src = await srcQuery.ToListAsync(ct);

            var existing = await db.UserPermissions.AsNoTracking()
                .Where(p => p.UserId == req.ToUserId)
                .Select(p => new { p.Id, p.ShareId, p.AllowedPath })
                .ToListAsync(ct);

            var result = new CopyUserPermissionsResult();
            var now = DateTime.UtcNow;
            var createdBy = principal.GetUserId();
            foreach (var s in src)
            {
                var dup = existing.FirstOrDefault(x => x.ShareId == s.ShareId &&
                    string.Equals(x.AllowedPath, s.AllowedPath, StringComparison.OrdinalIgnoreCase));
                if (dup is not null)
                {
                    if (!req.Overwrite) { result.Skipped++; continue; }
                    var tracked = await db.UserPermissions.FirstAsync(p => p.Id == dup.Id, ct);
                    tracked.TemplateId = s.TemplateId;
                    tracked.DisplayName = s.DisplayName;
                    result.Updated++;
                }
                else
                {
                    db.UserPermissions.Add(new UserPermission
                    {
                        UserId = req.ToUserId, ShareId = s.ShareId, TemplateId = s.TemplateId,
                        AllowedPath = s.AllowedPath, DisplayName = s.DisplayName,
                        CreatedAt = now, CreatedBy = createdBy,
                    });
                    result.Copied++;
                }
            }
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.PermissionCopy,
                $"from:{req.FromUserId},to:{req.ToUserId},copied:{result.Copied},updated:{result.Updated},skipped:{result.Skipped}", ct: ct);
            return Results.Ok(result);
        });

        return app;
    }
}
