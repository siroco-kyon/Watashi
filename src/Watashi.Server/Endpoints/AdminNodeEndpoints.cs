using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminNodeEndpoints
{
    public static IEndpointRouteBuilder MapAdminNodeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/nodes").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var nodes = await db.ExecutionNodes.AsNoTracking().Select(n => new NodeDto
            {
                Id = n.Id, Name = n.Name, NodeType = n.NodeType, Endpoint = n.Endpoint,
                ClientCertificateThumbprint = n.ClientCertificateThumbprint, IsActive = n.IsActive,
                LastHeartbeatAt = n.LastHeartbeatAt, HealthStatus = n.HealthStatus,
                MaxConcurrency = n.MaxConcurrency, CreatedAt = n.CreatedAt,
            }).ToListAsync(ct);
            return Results.Ok(nodes);
        });

        group.MapPost("/", async (CreateNodeRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (req.NodeType != NodeTypes.Direct && req.NodeType != NodeTypes.Agent)
                return Results.BadRequest(new { error = "NodeType は Direct または Agent" });
            var n = new ExecutionNode
            {
                Name = req.Name,
                NodeType = req.NodeType,
                Endpoint = req.Endpoint,
                ClientCertificateThumbprint = req.ClientCertificateThumbprint,
                IsActive = true,
                HealthStatus = req.NodeType == NodeTypes.Direct ? HealthStatuses.Healthy : HealthStatuses.Unknown,
                MaxConcurrency = req.MaxConcurrency,
                CreatedAt = DateTime.UtcNow,
            };
            db.ExecutionNodes.Add(n);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.NodeCreate, $"node:{n.Id}", ct: ct);
            return Results.Created($"/api/admin/nodes/{n.Id}", new { id = n.Id });
        });

        group.MapPatch("/{id:int}", async (int id, UpdateNodeRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var n = await db.ExecutionNodes.FindAsync(new object?[] { id }, ct);
            if (n is null) return Results.NotFound();
            if (req.Name is not null) n.Name = req.Name;
            if (req.Endpoint is not null) n.Endpoint = req.Endpoint;
            if (req.ClientCertificateThumbprint is not null) n.ClientCertificateThumbprint = req.ClientCertificateThumbprint;
            if (req.IsActive.HasValue) n.IsActive = req.IsActive.Value;
            if (req.MaxConcurrency.HasValue) n.MaxConcurrency = req.MaxConcurrency.Value;
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.NodeUpdate, $"node:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var n = await db.ExecutionNodes.FindAsync(new object?[] { id }, ct);
            if (n is null) return Results.NotFound();
            var hostsUsing = await db.CifsHosts.AsNoTracking().AnyAsync(h => h.ExecutionNodeId == id, ct);
            if (hostsUsing) return Results.BadRequest(new { error = "このノードを使用するホストがあるため削除できません" });
            db.ExecutionNodes.Remove(n);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.NodeDelete, $"node:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/regenerate-key", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var n = await db.ExecutionNodes.FindAsync(new object?[] { id }, ct);
            if (n is null) return Results.NotFound();
            n.ClientCertificateThumbprint = null;
            n.HealthStatus = HealthStatuses.Unknown;
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.NodeRegenerateKey, $"node:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/status", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var n = await db.ExecutionNodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (n is null) return Results.NotFound();
            return Results.Ok(new { id = n.Id, n.HealthStatus, n.LastHeartbeatAt, n.IsActive });
        });

        return app;
    }
}
