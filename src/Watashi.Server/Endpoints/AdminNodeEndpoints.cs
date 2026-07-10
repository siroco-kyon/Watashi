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
            var nameError = await ValidateNodeNameAsync(db, req.Name, currentNodeId: null, ct);
            if (nameError is not null) return Results.BadRequest(new { error = nameError });
            var gatewayError = await ValidateGatewayAsync(
                db, req.NodeType, req.Endpoint, req.GatewayNodeId, currentNodeId: null, ct);
            if (gatewayError is not null) return Results.BadRequest(new { error = gatewayError });
            var maxConcurrency = req.MaxConcurrency ?? await GetDefaultMaxConcurrencyAsync(db, ct);
            var concurrencyError = ValidateMaxConcurrency(maxConcurrency);
            if (concurrencyError is not null) return Results.BadRequest(new { error = concurrencyError });
            var n = new ExecutionNode
            {
                Name = req.Name.Trim(),
                NodeType = req.NodeType,
                Endpoint = req.Endpoint,
                ClientCertificateThumbprint = req.ClientCertificateThumbprint,
                GatewayNodeId = req.GatewayNodeId,
                IsActive = true,
                HealthStatus = req.NodeType == NodeTypes.Direct ? HealthStatuses.Healthy : HealthStatuses.Unknown,
                MaxConcurrency = maxConcurrency,
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
            if (req.Name is not null)
            {
                var nameError = await ValidateNodeNameAsync(db, req.Name, currentNodeId: id, ct);
                if (nameError is not null) return Results.BadRequest(new { error = nameError });
                n.Name = req.Name.Trim();
            }
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
            if (req.MaxConcurrency.HasValue)
            {
                var concurrencyError = ValidateMaxConcurrency(req.MaxConcurrency.Value);
                if (concurrencyError is not null) return Results.BadRequest(new { error = concurrencyError });
                n.MaxConcurrency = req.MaxConcurrency.Value;
            }
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

    /// <summary>
    /// ノード名の必須 + 一意性検証。Agent の heartbeat は AgentId とノード Name の完全一致で
    /// 対象ノードを特定するため、同名ノードが複数あると健康状態の更新先が不定になる。
    /// 前後空白も同様に照合失敗の原因になるため、保存時は Trim した名前で比較・登録する。
    /// </summary>
    internal static async Task<string?> ValidateNodeNameAsync(
        AppDbContext db, string? name, int? currentNodeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "ノード名を入力してください。";
        var trimmed = name.Trim();
        var duplicate = await db.ExecutionNodes.AsNoTracking()
            .AnyAsync(n => n.Name == trimmed && (currentNodeId == null || n.Id != currentNodeId.Value), ct);
        if (duplicate)
            return "同名のノードが既に存在します。Agent の heartbeat はノード名で照合されるため、名前は一意にしてください。";
        return null;
    }

    internal static async Task<string?> ValidateGatewayAsync(
        AppDbContext db, string nodeType, string? endpoint, int? gatewayNodeId, int? currentNodeId, CancellationToken ct)
    {
        if (gatewayNodeId is null) return null;
        if (nodeType != NodeTypes.Agent)
            return "経由 Agent は Agent タイプのノードにのみ設定できます。";
        if (currentNodeId.HasValue && gatewayNodeId.Value == currentNodeId.Value)
            return "自分自身を経由 Agent にはできません。";
        // 自ノードが既に他ノードの経由 Agent として使われている場合、ここに経由 Agent を
        // 設定すると「B → 自ノード → C」の 2 段チェーンが事後的に成立してしまうため拒否する。
        if (currentNodeId.HasValue &&
            await db.ExecutionNodes.AsNoTracking().AnyAsync(n => n.GatewayNodeId == currentNodeId.Value, ct))
            return "このノードは他ノードの経由 Agent として使用されているため、経由 Agent を設定できません (1段チェーンのみ対応)。";
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

    internal static string? ValidateMaxConcurrency(int value) =>
        value is < 1 or > 100_000
            ? "MaxConcurrency は 1 以上 100000 以下で指定してください。"
            : null;

    private static async Task<int> GetDefaultMaxConcurrencyAsync(AppDbContext db, CancellationToken ct)
    {
        var setting = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.AgentMaxConcurrency, ct);
        return setting is not null && int.TryParse(setting.Value, out var value) && ValidateMaxConcurrency(value) is null
            ? value
            : 20;
    }

    private static bool IsHttpOrHttpsUrl(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
