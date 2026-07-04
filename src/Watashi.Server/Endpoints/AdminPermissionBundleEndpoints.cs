using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminPermissionBundleEndpoints
{
    public static IEndpointRouteBuilder MapAdminPermissionBundleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/permission-bundles").RequireAuthorization("Admin");

        // ===== List =====
        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var bundles = await db.PermissionBundles.AsNoTracking()
                .OrderBy(b => b.Name)
                .Select(b => new PermissionBundleDto
                {
                    Id = b.Id,
                    Name = b.Name,
                    Description = b.Description,
                    CreatedAt = b.CreatedAt,
                    Entries = b.Entries.Select(e => new PermissionBundleEntryDto
                    {
                        Id = e.Id,
                        ShareId = e.ShareId,
                        ShareName = e.Share!.DisplayName,
                        HostName = e.Share.Host!.Name,
                        TemplateId = e.TemplateId,
                        TemplateName = e.Template!.Name,
                        AllowedPath = e.AllowedPath,
                        DisplayName = e.DisplayName,
                    }).ToList(),
                })
                .ToListAsync(ct);
            return Results.Ok(bundles);
        });

        // ===== Get one =====
        group.MapGet("/{id:int}", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var b = await db.PermissionBundles.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new PermissionBundleDto
                {
                    Id = x.Id, Name = x.Name, Description = x.Description, CreatedAt = x.CreatedAt,
                    Entries = x.Entries.Select(e => new PermissionBundleEntryDto
                    {
                        Id = e.Id, ShareId = e.ShareId, ShareName = e.Share!.DisplayName,
                        HostName = e.Share.Host!.Name, TemplateId = e.TemplateId,
                        TemplateName = e.Template!.Name, AllowedPath = e.AllowedPath, DisplayName = e.DisplayName,
                    }).ToList(),
                })
                .FirstOrDefaultAsync(ct);
            return b is null ? Results.NotFound() : Results.Ok(b);
        });

        // ===== Create =====
        group.MapPost("/", async (CreatePermissionBundleRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name は必須です。" });
            var bundle = new PermissionBundle
            {
                Name = req.Name.Trim(),
                Description = req.Description,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = principal.GetUserId(),
                Entries = req.Entries.Select(e => new PermissionBundleEntry
                {
                    ShareId = e.ShareId,
                    TemplateId = e.TemplateId,
                    AllowedPath = PathHelper.NormalizePath(e.AllowedPath),
                    DisplayName = e.DisplayName,
                }).ToList(),
            };
            db.PermissionBundles.Add(bundle);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                await audit.LogAdminAsync(principal, ctx, AdminOperations.BundleCreate, $"bundle:{req.Name}", AuditResults.Failure, "duplicate_or_fk", ct);
                return Results.BadRequest(new { error = "同名の権限セットが既にあるか、参照先 (Share/Template) が無効です。" });
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.BundleCreate, $"bundle:{bundle.Id}", ct: ct);
            return Results.Created($"/api/admin/permission-bundles/{bundle.Id}", new { id = bundle.Id });
        });

        // ===== Update (name/desc + replace entries) =====
        group.MapPatch("/{id:int}", async (int id, UpdatePermissionBundleRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var bundle = await db.PermissionBundles.Include(x => x.Entries).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (bundle is null) return Results.NotFound();
            if (req.Name is not null) bundle.Name = req.Name.Trim();
            if (req.Description is not null) bundle.Description = req.Description;
            if (req.Entries is not null)
            {
                // 全件置換 (シンプル運用)。差分更新が必要になったら別途。
                bundle.Entries.Clear();
                foreach (var e in req.Entries)
                    bundle.Entries.Add(new PermissionBundleEntry
                    {
                        ShareId = e.ShareId, TemplateId = e.TemplateId,
                        AllowedPath = PathHelper.NormalizePath(e.AllowedPath), DisplayName = e.DisplayName,
                    });
            }
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                await audit.LogAdminAsync(principal, ctx, AdminOperations.BundleUpdate, $"bundle:{id}", AuditResults.Failure, "duplicate_or_fk", ct);
                return Results.BadRequest(new { error = "同名の権限セットが既にあるか、参照先 (Share/Template) が無効です。" });
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.BundleUpdate, $"bundle:{id}", ct: ct);
            return Results.NoContent();
        });

        // ===== Delete =====
        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var bundle = await db.PermissionBundles.FindAsync(new object?[] { id }, ct);
            if (bundle is null) return Results.NotFound();
            db.PermissionBundles.Remove(bundle);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.BundleDelete, $"bundle:{id}", ct: ct);
            return Results.NoContent();
        });

        // ===== Apply to user =====
        group.MapPost("/{id:int}/apply", async (int id, ApplyPermissionBundleRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var bundle = await db.PermissionBundles.AsNoTracking()
                .Include(x => x.Entries)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (bundle is null) return Results.NotFound();
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == req.UserId, ct))
                return Results.BadRequest(new { error = "User が存在しません。" });
            var existing = await db.UserPermissions.AsNoTracking()
                .Where(p => p.UserId == req.UserId)
                .Select(p => new { p.Id, p.ShareId, p.AllowedPath })
                .ToListAsync(ct);
            var result = new ApplyPermissionBundleResult();
            var now = DateTime.UtcNow;
            var createdBy = principal.GetUserId();
            foreach (var e in bundle.Entries)
            {
                var norm = PathHelper.NormalizePath(e.AllowedPath);
                var dup = existing.FirstOrDefault(x => x.ShareId == e.ShareId &&
                    string.Equals(x.AllowedPath, norm, StringComparison.OrdinalIgnoreCase));
                if (dup is not null)
                {
                    if (!req.Overwrite) { result.Skipped++; continue; }
                    var tracked = await db.UserPermissions.FirstAsync(p => p.Id == dup.Id, ct);
                    tracked.TemplateId = e.TemplateId;
                    tracked.DisplayName = e.DisplayName;
                    result.Updated++;
                }
                else
                {
                    db.UserPermissions.Add(new UserPermission
                    {
                        UserId = req.UserId, ShareId = e.ShareId, TemplateId = e.TemplateId,
                        AllowedPath = norm, DisplayName = e.DisplayName,
                        CreatedAt = now, CreatedBy = createdBy,
                    });
                    result.Created++;
                }
            }
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.BundleApply,
                $"bundle:{id},user:{req.UserId},created:{result.Created},updated:{result.Updated},skipped:{result.Skipped}", ct: ct);
            return Results.Ok(result);
        });

        return app;
    }
}
