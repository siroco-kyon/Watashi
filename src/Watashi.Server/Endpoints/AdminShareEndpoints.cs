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
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // (HostId, ShareName) のユニーク制約違反。ChangeTracker を掃除しないと
                // 後続の監査ログ SaveChanges で同じ例外が再発する。
                db.ChangeTracker.Clear();
                await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareCreate, $"share:{req.ShareName}", AuditResults.Failure, "share_conflict", ct);
                return Results.BadRequest(new { error = "同じホストに同名の共有が既にあります。" });
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareCreate, $"share:{s.Id}", ct: ct);
            return Results.Created($"/api/admin/shares/{s.Id}", new { id = s.Id });
        });

        group.MapPatch("/{id:int}", async (int id, UpdateShareRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            using var shareGate = await DurableShareLock.AcquireAsync(id, ct);
            var s = await db.CifsShares.FindAsync(new object?[] { id }, ct);
            if (s is null) return Results.NotFound();
            var requestedShareName = req.ShareName?.Trim();
            var changesPhysicalLocation =
                (req.HostId.HasValue && req.HostId.Value != s.HostId) ||
                (requestedShareName is not null &&
                 !string.Equals(requestedShareName, s.ShareName, StringComparison.OrdinalIgnoreCase));
            if (changesPhysicalLocation && await HasDurableTransferStateAsync(db, id, ct))
            {
                await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareUpdate,
                    $"share:{id}", AuditResults.Failure, "share_has_durable_state", ct);
                return Results.Conflict(new
                {
                    error = "進行中または回収待ちの転送・ごみ箱データがあるため、共有の接続先を変更できません。",
                    code = "share_has_durable_state",
                });
            }
            if (req.HostId.HasValue)
            {
                if (!await db.CifsHosts.AsNoTracking().AnyAsync(h => h.Id == req.HostId.Value, ct))
                    return Results.BadRequest(new { error = "Host 不在" });
                s.HostId = req.HostId.Value;
            }
            if (req.ShareName is not null)
            {
                if (string.IsNullOrWhiteSpace(req.ShareName))
                    return Results.BadRequest(new { error = "ShareName 必須" });
                s.ShareName = requestedShareName!;
            }
            if (req.DisplayName is not null)
                s.DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? s.ShareName : req.DisplayName.Trim();
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareUpdate, $"share:{id}", AuditResults.Failure, "share_conflict", ct);
                return Results.BadRequest(new { error = "同じホストに同名の共有が既にあります。" });
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareUpdate, $"share:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            using var shareGate = await DurableShareLock.AcquireAsync(id, ct);
            var s = await db.CifsShares.FindAsync(new object?[] { id }, ct);
            if (s is null) return Results.NotFound();
            if (await HasDurableTransferStateAsync(db, id, ct))
            {
                await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareDelete,
                    $"share:{id}", AuditResults.Failure, "share_has_durable_state", ct);
                return Results.Conflict(new
                {
                    error = "進行中または回収待ちの転送・ごみ箱データがあるため、共有を削除できません。",
                    code = "share_has_durable_state",
                });
            }
            db.CifsShares.Remove(s);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareDelete, $"share:{id}", ct: ct);
            return Results.NoContent();
        });

        return app;
    }

    internal static async Task<bool> HasDurableTransferStateAsync(
        AppDbContext db,
        int shareId,
        CancellationToken ct)
    {
        var hasUploadState = await db.UploadSessions.AsNoTracking().AnyAsync(s =>
            s.ShareId == shareId &&
            (s.Status == UploadSessionStatuses.Active ||
             s.Status == UploadSessionStatuses.Committing ||
             s.Status == UploadSessionStatuses.Failed), ct);
        if (hasUploadState) return true;

        return await db.RemoteTrashEntries.AsNoTracking().AnyAsync(e =>
            e.ShareId == shareId &&
            (e.Status == RemoteTrashStatuses.Trashing ||
             e.Status == RemoteTrashStatuses.Active ||
             e.Status == RemoteTrashStatuses.Restoring ||
             e.Status == RemoteTrashStatuses.Purging ||
             e.Status == RemoteTrashStatuses.Failed), ct);
    }
}
