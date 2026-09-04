using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
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

        // 共有の廃止・付け替えを管理者が完遂できるようにするための2本。
        // PATCH / DELETE 側のガードは残したまま、解除は明示操作として分離する。
        group.MapGet("/{id:int}/durable-state", async (
            int id, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.CifsShares.AsNoTracking().AnyAsync(x => x.Id == id, ct))
                return Results.NotFound();
            return Results.Ok(await ReadDurableStateAsync(db, id, ct));
        });

        group.MapPost("/{id:int}/durable-state/release", async (
            int id, ReleaseDurableStateRequest req, AppDbContext db,
            UploadSessionService uploads, RemoteTrashService trash,
            AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var actorUserId)) return Results.Unauthorized();
            if (!req.Confirm)
                return Results.BadRequest(new { error = "確認されていない強制解除は実行できません。" });
            if (!await db.CifsShares.AsNoTracking().AnyAsync(x => x.Id == id, ct))
                return Results.NotFound();

            // 列挙と終端の間に新しい転送/ごみ箱が作られると取りこぼし、解除に成功したのに
            // 続く変更・削除がまたブロックされる。作成側と同じ共有単位 lock を全体で保持する。
            using var shareGate = await DurableShareLock.AcquireAsync(id, ct);
            var uploadResult = await uploads.ReleaseForShareAsync(id, ct);
            var trashResult = await trash.ReleaseForShareAsync(id, actorUserId, ct);

            var result = new ReleaseDurableStateResult
            {
                CleanedUp = uploadResult.CleanedUp + trashResult.CleanedUp,
                Abandoned = uploadResult.Abandoned + trashResult.Abandoned,
                OrphanedPaths = uploadResult.OrphanedPaths.Concat(trashResult.OrphanedPaths).ToList(),
            };

            var reason = string.IsNullOrWhiteSpace(req.Reason) ? "(理由未記入)" : req.Reason.Trim();
            await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareReleaseDurableState,
                $"share:{id}",
                result.Abandoned > 0 ? AuditResults.Warning : AuditResults.Success,
                $"cleaned={result.CleanedUp} abandoned={result.Abandoned} reason={reason}", ct);

            // 回収できなかった実体は共有上にゴミとして残る。後から追跡できるよう
            // パスを1件ずつ監査ログに残す (件数は共有あたり高々数件〜数十件)。
            foreach (var path in result.OrphanedPaths.Take(MaxAuditedOrphanPaths))
            {
                await audit.LogAdminAsync(principal, ctx, AdminOperations.ShareReleaseDurableState,
                    $"share:{id}{path}", AuditResults.Warning, "orphaned_path", ct);
            }
            return Results.Ok(result);
        });

        return app;
    }

    /// <summary>放棄パスを個別に監査へ残す上限。異常件数でログを溢れさせない。</summary>
    private const int MaxAuditedOrphanPaths = 200;

    internal static async Task<ShareDurableStateDto> ReadDurableStateAsync(
        AppDbContext db, int shareId, CancellationToken ct)
    {
        var uploads = await (from u in db.UploadSessions.AsNoTracking()
                             join usr in db.Users.AsNoTracking() on u.UserId equals usr.Id into gj
                             from usr in gj.DefaultIfEmpty()
                             where u.ShareId == shareId &&
                                   (u.Status == UploadSessionStatuses.Active ||
                                    u.Status == UploadSessionStatuses.Committing ||
                                    u.Status == UploadSessionStatuses.Failed)
                             orderby u.CreatedAt
                             select new DurableUploadDto
                             {
                                 Id = u.Id,
                                 Username = usr != null ? usr.Username : "(不明)",
                                 TargetPath = u.TargetPath,
                                 TempPath = u.TempPath,
                                 Status = u.Status,
                                 ErrorCode = u.ErrorCode,
                                 UpdatedAt = u.UpdatedAt,
                                 ExpiresAt = u.ExpiresAt,
                             }).ToListAsync(ct);

        var trash = await db.RemoteTrashEntries.AsNoTracking()
            .Where(e => e.ShareId == shareId &&
                        (e.Status == RemoteTrashStatuses.Trashing ||
                         e.Status == RemoteTrashStatuses.Active ||
                         e.Status == RemoteTrashStatuses.Restoring ||
                         e.Status == RemoteTrashStatuses.Purging ||
                         e.Status == RemoteTrashStatuses.Failed))
            .OrderBy(e => e.DeletedAt)
            .Select(e => new DurableTrashDto
            {
                Id = e.Id,
                OriginalPath = e.OriginalPath,
                TrashPath = e.TrashPath,
                Status = e.Status,
                ErrorCode = e.ErrorCode,
                ExpiresAt = e.ExpiresAt,
            })
            .ToListAsync(ct);

        return new ShareDurableStateDto
        {
            ShareId = shareId,
            Uploads = uploads,
            Trash = trash,
            BlocksPhysicalChange = uploads.Count > 0 || trash.Count > 0,
            StuckCount =
                uploads.Count(u => u.ErrorCode == TransferCleanupErrorCodes.CleanupRetry) +
                trash.Count(t => t.ErrorCode == "purge_retry"),
        };
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
