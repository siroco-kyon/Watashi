using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Server.Services.Cifs;
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
            var items = await (from h in db.CifsHosts
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

        group.MapPost("/", async (CreateHostRequest req, AppDbContext db, EncryptionService enc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.HostAddress))
                return Results.BadRequest(new { error = "Name/HostAddress は必須です。" });
            if (!await db.ExecutionNodes.AnyAsync(n => n.Id == req.ExecutionNodeId, ct))
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
            return Results.Created($"/api/admin/hosts/{h.Id}", new { id = h.Id });
        });

        group.MapPatch("/{id:int}", async (int id, UpdateHostRequest req, AppDbContext db, EncryptionService enc, CancellationToken ct) =>
        {
            var h = await db.CifsHosts.FindAsync(new object?[] { id }, ct);
            if (h is null) return Results.NotFound();
            if (req.Name is not null) h.Name = req.Name;
            if (req.HostAddress is not null) h.HostAddress = req.HostAddress;
            if (req.Port.HasValue) h.Port = req.Port.Value;
            if (req.Description is not null) h.Description = req.Description;
            if (req.CredUsername is not null) h.CredUsername = req.CredUsername;
            if (!string.IsNullOrEmpty(req.CredPassword)) h.CredPasswordEnc = enc.Encrypt(req.CredPassword);
            if (req.ExecutionNodeId.HasValue) h.ExecutionNodeId = req.ExecutionNodeId.Value;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var h = await db.CifsHosts.FindAsync(new object?[] { id }, ct);
            if (h is null) return Results.NotFound();
            db.CifsHosts.Remove(h);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/test", async (int id, AppDbContext db, EncryptionService enc, CifsService cifs, CancellationToken ct) =>
        {
            var h = await db.CifsHosts.FindAsync(new object?[] { id }, ct);
            if (h is null) return Results.NotFound();
            var anyShare = await db.CifsShares.FirstOrDefaultAsync(s => s.HostId == id, ct);
            if (anyShare is null) return Results.BadRequest(new { error = "テスト用の共有が登録されていません。" });
            var info = new CifsConnectionInfo(h.HostAddress, h.Port, h.CredUsername, enc.Decrypt(h.CredPasswordEnc), anyShare.ShareName);
            var ok = await Task.Run(() => cifs.TestConnection(info), ct);
            return Results.Ok(new { ok });
        });

        return app;
    }
}
