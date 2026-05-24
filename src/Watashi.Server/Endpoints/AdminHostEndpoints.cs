using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminHostEndpoints
{
    public static IEndpointRouteBuilder MapAdminHostEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/hosts").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await (from h in db.CifsHosts.AsNoTracking()
                join n in db.ExecutionNodes on h.ExecutionNodeId equals n.Id
                select new HostDto
                {
                    Id = h.Id,
                    Name = h.Name,
                    HostAddress = h.HostAddress,
                    Port = h.Port,
                    Description = h.Description,
                    CredUsername = h.CredUsername,
                    ExecutionNodeId = n.Id,
                    ExecutionNodeName = n.Name,
                    CreatedAt = h.CreatedAt,
                }).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/", async (CreateHostRequest req, AppDbContext db, EncryptionService enc, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.HostAddress))
                return Results.BadRequest(new { error = "Name/HostAddress は必須です。" });
            if (!IsSupportedSmbPort(req.Port))
                return Results.BadRequest(new { error = "Port は 445 (DirectTCP) または 139 (NetBIOS over TCP) のみ指定できます。" });
            if (!await db.ExecutionNodes.AsNoTracking().AnyAsync(n => n.Id == req.ExecutionNodeId, ct))
                return Results.BadRequest(new { error = "指定された ExecutionNode が存在しません。" });
            var h = new CifsHost
            {
                Name = req.Name,
                HostAddress = req.HostAddress,
                Port = req.Port,
                Description = req.Description,
                CredUsername = req.CredUsername,
                CredPasswordEnc = enc.Encrypt(req.CredPassword ?? string.Empty),
                ExecutionNodeId = req.ExecutionNodeId,
                CreatedAt = DateTime.UtcNow,
            };
            db.CifsHosts.Add(h);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.HostCreate, $"host:{h.Id}", ct: ct);
            return Results.Created($"/api/admin/hosts/{h.Id}", new { id = h.Id });
        });

        group.MapPatch("/{id:int}", async (int id, UpdateHostRequest req, AppDbContext db, EncryptionService enc, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var h = await db.CifsHosts.FindAsync(new object?[] { id }, ct);
            if (h is null) return Results.NotFound();
            if (req.Port.HasValue && !IsSupportedSmbPort(req.Port.Value))
                return Results.BadRequest(new { error = "Port は 445 (DirectTCP) または 139 (NetBIOS over TCP) のみ指定できます。" });
            if (req.Name is not null) h.Name = req.Name;
            if (req.HostAddress is not null) h.HostAddress = req.HostAddress;
            if (req.Port.HasValue) h.Port = req.Port.Value;
            if (req.Description is not null) h.Description = req.Description;
            if (req.CredUsername is not null) h.CredUsername = req.CredUsername;
            if (!string.IsNullOrEmpty(req.CredPassword)) h.CredPasswordEnc = enc.Encrypt(req.CredPassword);
            if (req.ExecutionNodeId.HasValue) h.ExecutionNodeId = req.ExecutionNodeId.Value;
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.HostUpdate, $"host:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var h = await db.CifsHosts.FindAsync(new object?[] { id }, ct);
            if (h is null) return Results.NotFound();
            db.CifsHosts.Remove(h);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.HostDelete, $"host:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/test", async (int id, AppDbContext db, EncryptionService enc, NodeRouter router, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var h = await db.CifsHosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (h is null) return Results.NotFound();
            var anyShare = await db.CifsShares.AsNoTracking().FirstOrDefaultAsync(s => s.HostId == id, ct);
            if (anyShare is null) return Results.BadRequest(new { error = "テスト用の共有が登録されていません。" });
            var node = await db.ExecutionNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == h.ExecutionNodeId, ct);
            if (node is null) return Results.BadRequest(new { error = "ホストに紐づく ExecutionNode が見つかりません。" });
            var info = new CifsConnectionInfo(h.HostAddress, h.Port, h.CredUsername, enc.Decrypt(h.CredPasswordEnc), anyShare.ShareName);
            var errorMessage = default(string);
            var ok = false;
            try
            {
                ok = await router.TestAsync(node, info, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.HostTest, $"host:{id}",
                ok ? AuditResults.Success : AuditResults.Failure, errorMessage, ct);
            return Results.Ok(new { ok });
        });

        return app;
    }

    /// <summary>
    /// SMBLibrary 1.5.x の制約: Connect(IPAddress, transport) は port を取らず、transport ごとに
    /// 既定ポート (DirectTCP=445, NetBIOS=139) を内部で使う。それ以外は実接続できないので登録段階で弾く。
    /// </summary>
    private static bool IsSupportedSmbPort(int port) => port == 445 || port == 139;
}
