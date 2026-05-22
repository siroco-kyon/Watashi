using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Auth;
using Watashi.Server.Data;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        // mTLS でクライアント証明書を要求し、AgentCertificateValidator が ExecutionNode と照合する。
        // 認証スキームは "Certificate" 固定で、JWT は受け付けない。
        var group = app.MapGroup("/api/internal").RequireAuthorization("Agent");

        group.MapPost("/heartbeat", async (HeartbeatRequest req, ClaimsPrincipal principal, AppDbContext db, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Internal");
            if (string.IsNullOrWhiteSpace(req.AgentId))
                return Results.BadRequest(new { error = "AgentId が必要です。" });
            var certAgent = principal.FindFirst(AgentCertificateValidator.AgentIdClaim)?.Value;
            if (!string.Equals(certAgent, req.AgentId, StringComparison.Ordinal))
            {
                logger.LogWarning("Heartbeat AgentId 不一致 cert={Cert} body={Body}", certAgent, req.AgentId);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var node = await db.ExecutionNodes.FirstOrDefaultAsync(n => n.Name == req.AgentId, ct);
            if (node is null) return Results.NotFound(new { error = "Unknown agent" });
            node.LastHeartbeatAt = req.Timestamp == default ? DateTime.UtcNow : req.Timestamp;
            node.HealthStatus = HealthStatuses.Healthy;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/audit-logs/batch", async (AuditBatchRequest body, ClaimsPrincipal principal, AppDbContext db, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Internal");
            if (body.Items is null || body.Items.Length == 0) return Results.NoContent();
            var certAgent = principal.FindFirst(AgentCertificateValidator.AgentIdClaim)?.Value;
            int added = 0, skipped = 0;
            foreach (var json in body.Items)
            {
                if (string.IsNullOrWhiteSpace(json)) continue;
                try
                {
                    var log = JsonSerializer.Deserialize<AuditLog>(json, JsonOpts);
                    if (log is null) { skipped++; continue; }
                    log.Id = 0;
                    if (log.Timestamp == default) log.Timestamp = DateTime.UtcNow;
                    db.AuditLogs.Add(log);
                    added++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    logger.LogWarning(ex, "audit-log バッチ内のレコード解析に失敗 agent={Agent} len={Len}", certAgent, json.Length);
                }
            }
            await db.SaveChangesAsync(ct);
            if (skipped > 0)
                logger.LogWarning("audit-log バッチ: agent={Agent} added={Added} skipped={Skipped}", certAgent, added, skipped);
            return Results.Ok(new { added, skipped });
        });

        return app;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public record HeartbeatRequest(string AgentId, DateTime Timestamp);
    public record AuditBatchRequest(string[] Items);
}
