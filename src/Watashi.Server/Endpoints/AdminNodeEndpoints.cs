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
            var nodes = await (
                from n in db.ExecutionNodes.AsNoTracking()
                join g in db.ExecutionNodes.AsNoTracking() on n.GatewayNodeId equals g.Id into gatewayJoin
                from g in gatewayJoin.DefaultIfEmpty()
                select new NodeDto
                {
                    Id = n.Id, Name = n.Name, NodeType = n.NodeType, Endpoint = n.Endpoint,
                    ClientCertificateThumbprint = n.ClientCertificateThumbprint,
                    GatewayNodeId = n.GatewayNodeId,
                    GatewayNodeName = g == null ? null : g.Name,
                    IsActive = n.IsActive,
                    LastHeartbeatAt = n.LastHeartbeatAt, HealthStatus = n.HealthStatus,
                    MaxConcurrency = n.MaxConcurrency, CreatedAt = n.CreatedAt,
                }).ToListAsync(ct);
            return Results.Ok(nodes);
        });

        group.MapPost("/", async (CreateNodeRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (req.NodeType != NodeTypes.Direct && req.NodeType != NodeTypes.Agent)
                return Results.BadRequest(new { error = "NodeType は Direct または Agent" });
            var gatewayError = await ValidateGatewayAsync(
                db, req.NodeType, req.Endpoint, req.GatewayNodeId, currentNodeId: null, ct);
            if (gatewayError is not null) return Results.BadRequest(new { error = gatewayError });
            var n = new ExecutionNode
            {
                Name = req.Name,
                NodeType = req.NodeType,
                Endpoint = req.Endpoint,
                ClientCertificateThumbprint = req.ClientCertificateThumbprint,
                GatewayNodeId = req.GatewayNodeId,
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
            if (req.ClearGatewayNode == true)
            {
                n.GatewayNodeId = null;
            }
            else if (req.GatewayNodeId.HasValue)
            {
                var gatewayError = await ValidateGatewayAsync(
                    db, n.NodeType, n.Endpoint, req.GatewayNodeId, currentNodeId: n.Id, ct);
                if (gatewayError is not null) return Results.BadRequest(new { error = gatewayError });
                n.GatewayNodeId = req.GatewayNodeId;
            }
            else if (n.GatewayNodeId.HasValue && req.Endpoint is not null)
            {
                var gatewayError = await ValidateGatewayAsync(
                    db, n.NodeType, n.Endpoint, n.GatewayNodeId, currentNodeId: n.Id, ct);
                if (gatewayError is not null) return Results.BadRequest(new { error = gatewayError });
            }
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
            var gatewayUsing = await db.ExecutionNodes.AsNoTracking().AnyAsync(x => x.GatewayNodeId == id, ct);
            if (gatewayUsing) return Results.BadRequest(new { error = "このノードを経由 Agent として使用するノードがあるため削除できません" });
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

    internal static async Task<string?> ValidateGatewayAsync(
        AppDbContext db, string nodeType, string? endpoint, int? gatewayNodeId, int? currentNodeId, CancellationToken ct)
    {
        if (gatewayNodeId is null) return null;
        if (nodeType != NodeTypes.Agent)
            return "経由 Agent は Agent タイプのノードにのみ設定できます。";
        if (currentNodeId.HasValue && gatewayNodeId.Value == currentNodeId.Value)
            return "自分自身を経由 Agent にはできません。";
        if (string.IsNullOrWhiteSpace(endpoint))
            return "経由 Agent を使うノードでは Endpoint を入力してください。";
        if (!IsHttpOrHttpsUrl(endpoint))
            return "対象 Agent の Endpoint は http:// または https:// の URL で指定してください。";

        var gateway = await db.ExecutionNodes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == gatewayNodeId.Value, ct);
        if (gateway is null)
            return "指定された経由 Agent が存在しません。";
        if (gateway.NodeType != NodeTypes.Agent)
            return "経由 Agent には Agent タイプのノードを指定してください。";
        if (!gateway.IsActive)
            return "無効化されている Agent は経由 Agent に指定できません。";
        if (gateway.GatewayNodeId.HasValue)
            return "1段チェーンのみ対応です。経由 Agent 自体に別の経由 Agent は設定できません。";
        if (string.IsNullOrWhiteSpace(gateway.Endpoint))
            return "経由 Agent の Endpoint が未設定です。";
        if (!IsHttpOrHttpsUrl(gateway.Endpoint))
            return "経由 Agent の Endpoint は http:// または https:// の URL で指定してください。";

        return null;
    }

    private static bool IsHttpOrHttpsUrl(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
