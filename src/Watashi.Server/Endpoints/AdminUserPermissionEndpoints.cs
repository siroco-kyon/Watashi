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
            var now = DateTime.UtcNow;
            var rows = await LoadRowsAsync(db, userId, shareId, ct);
            var warningMap = UserPermissionRules.Analyze(rows.Select(ToGrant), now);
            return Results.Ok(rows.Select(row => ToDto(row, now, warningMap)).ToList());
        });

        // 実際の認可と同じ判定を使う read-only プレビュー。管理者が付与前後の結果と
        // 採用された PermissionId / 一致ルールを確認できる。
        group.MapGet("/simulate", async (
            int userId,
            int shareId,
            string? path,
            AppDbContext db,
            PermissionService permissions,
            CancellationToken ct) =>
        {
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct))
                return Results.BadRequest(new { error = "User が存在しません。" });
            if (!await db.CifsShares.AsNoTracking().AnyAsync(s => s.Id == shareId, ct))
                return Results.BadRequest(new { error = "Share が存在しません。" });
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path は必須です。" });

            return Results.Ok(await permissions.SimulateAsync(userId, shareId, path, ct: ct));
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
            if (!UserPermissionRules.TryNormalizeMetadata(
                    req.ValidFrom, req.ExpiresAt, req.Reason, req.TicketNumber,
                    out var metadata, out var validationError))
                return Results.BadRequest(new { error = validationError });

            var normalized = PathHelper.NormalizePath(req.AllowedPath);
            int? createdBy = principal.GetUserId();
            var entity = new UserPermission
            {
                UserId = req.UserId,
                ShareId = req.ShareId,
                TemplateId = req.TemplateId,
                AllowedPath = normalized,
                DisplayName = req.DisplayName,
                ValidFrom = metadata.ValidFrom,
                ExpiresAt = metadata.ExpiresAt,
                Reason = metadata.Reason,
                TicketNumber = metadata.TicketNumber,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = createdBy,
            };
            db.UserPermissions.Add(entity);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.PermissionCreate,
                $"perm:user={req.UserId},share={req.ShareId},path={normalized}", ct: ct);
            var warnings = await GetWarningsForAsync(db, entity.Id, entity.UserId, entity.ShareId, ct);
            return Results.Created($"/api/admin/user-permissions/{entity.Id}",
                new UserPermissionMutationResultDto { Id = entity.Id, Warnings = warnings });
        });

        group.MapPatch("/{id:int}", async (
            int id,
            UpdateUserPermissionRequest req,
            AppDbContext db,
            AuditLogService audit,
            HttpContext ctx,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var entity = await db.UserPermissions.FindAsync(new object?[] { id }, ct);
            if (entity is null) return Results.NotFound();
            if (!await db.CifsShares.AsNoTracking().AnyAsync(s => s.Id == req.ShareId, ct))
                return Results.BadRequest(new { error = "Share が存在しません。" });
            if (!await db.PermissionTemplates.AsNoTracking().AnyAsync(t => t.Id == req.TemplateId, ct))
                return Results.BadRequest(new { error = "Template が存在しません。" });
            if (!UserPermissionRules.TryNormalizeMetadata(
                    req.ValidFrom, req.ExpiresAt, req.Reason, req.TicketNumber,
                    out var metadata, out var validationError))
                return Results.BadRequest(new { error = validationError });

            entity.ShareId = req.ShareId;
            entity.TemplateId = req.TemplateId;
            entity.AllowedPath = PathHelper.NormalizePath(req.AllowedPath);
            entity.DisplayName = req.DisplayName;
            entity.ValidFrom = metadata.ValidFrom;
            entity.ExpiresAt = metadata.ExpiresAt;
            entity.Reason = metadata.Reason;
            entity.TicketNumber = metadata.TicketNumber;
            await db.SaveChangesAsync(ct);

            await audit.LogAdminAsync(principal, ctx, AdminOperations.PermissionUpdate,
                $"perm:{id},share:{entity.ShareId},path:{entity.AllowedPath}", ct: ct);
            var warnings = await GetWarningsForAsync(db, entity.Id, entity.UserId, entity.ShareId, ct);
            return Results.Ok(new UserPermissionMutationResultDto { Id = entity.Id, Warnings = warnings });
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
                    tracked.ValidFrom = s.ValidFrom;
                    tracked.ExpiresAt = s.ExpiresAt;
                    tracked.Reason = s.Reason;
                    tracked.TicketNumber = s.TicketNumber;
                    result.Updated++;
                }
                else
                {
                    db.UserPermissions.Add(new UserPermission
                    {
                        UserId = req.ToUserId,
                        ShareId = s.ShareId,
                        TemplateId = s.TemplateId,
                        AllowedPath = s.AllowedPath,
                        DisplayName = s.DisplayName,
                        ValidFrom = s.ValidFrom,
                        ExpiresAt = s.ExpiresAt,
                        Reason = s.Reason,
                        TicketNumber = s.TicketNumber,
                        CreatedAt = now,
                        CreatedBy = createdBy,
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

    private static async Task<List<PermissionAdminRow>> LoadRowsAsync(
        AppDbContext db,
        int? userId,
        int? shareId,
        CancellationToken ct)
        => await (
            from p in db.UserPermissions.AsNoTracking()
            join u in db.Users.AsNoTracking() on p.UserId equals u.Id
            join s in db.CifsShares.AsNoTracking() on p.ShareId equals s.Id
            join h in db.CifsHosts.AsNoTracking() on s.HostId equals h.Id
            join t in db.PermissionTemplates.AsNoTracking() on p.TemplateId equals t.Id
            where (userId == null || p.UserId == userId)
                  && (shareId == null || p.ShareId == shareId)
            select new PermissionAdminRow
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
                ValidFrom = p.ValidFrom,
                ExpiresAt = p.ExpiresAt,
                Reason = p.Reason,
                TicketNumber = p.TicketNumber,
                CreatedAt = p.CreatedAt,
                CanRead = t.CanRead,
                CanWrite = t.CanWrite,
                CanDelete = t.CanDelete,
                CanRename = t.CanRename,
            }).ToListAsync(ct);

    private static UserPermissionRules.Grant ToGrant(PermissionAdminRow row)
        => new(row.Id, row.UserId, row.ShareId, row.TemplateId, row.TemplateName, row.AllowedPath,
            row.ValidFrom, row.ExpiresAt, row.CanRead, row.CanWrite, row.CanDelete, row.CanRename);

    private static UserPermissionDto ToDto(
        PermissionAdminRow row,
        DateTime now,
        IReadOnlyDictionary<int, List<PermissionWarningDto>> warningMap)
        => new()
        {
            Id = row.Id,
            UserId = row.UserId,
            Username = row.Username,
            ShareId = row.ShareId,
            ShareName = row.ShareName,
            HostName = row.HostName,
            TemplateId = row.TemplateId,
            TemplateName = row.TemplateName,
            AllowedPath = row.AllowedPath,
            DisplayName = row.DisplayName,
            ValidFrom = row.ValidFrom,
            ExpiresAt = row.ExpiresAt,
            Reason = row.Reason,
            TicketNumber = row.TicketNumber,
            EffectiveStatus = UserPermissionRules.GetEffectiveStatus(row.ValidFrom, row.ExpiresAt, now),
            Warnings = warningMap.TryGetValue(row.Id, out var warnings) ? warnings : new List<PermissionWarningDto>(),
            CreatedAt = row.CreatedAt,
        };

    private static async Task<List<PermissionWarningDto>> GetWarningsForAsync(
        AppDbContext db,
        int permissionId,
        int userId,
        int shareId,
        CancellationToken ct)
    {
        var rows = await LoadRowsAsync(db, userId, shareId, ct);
        var warningMap = UserPermissionRules.Analyze(rows.Select(ToGrant), DateTime.UtcNow);
        return warningMap.TryGetValue(permissionId, out var warnings)
            ? warnings
            : new List<PermissionWarningDto>();
    }

    private sealed class PermissionAdminRow
    {
        public int Id { get; init; }
        public int UserId { get; init; }
        public string? Username { get; init; }
        public int ShareId { get; init; }
        public string? ShareName { get; init; }
        public string? HostName { get; init; }
        public int TemplateId { get; init; }
        public string? TemplateName { get; init; }
        public string AllowedPath { get; init; } = "/";
        public string? DisplayName { get; init; }
        public DateTime? ValidFrom { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? Reason { get; init; }
        public string? TicketNumber { get; init; }
        public DateTime CreatedAt { get; init; }
        public bool CanRead { get; init; }
        public bool CanWrite { get; init; }
        public bool CanDelete { get; init; }
        public bool CanRename { get; init; }
    }
}
