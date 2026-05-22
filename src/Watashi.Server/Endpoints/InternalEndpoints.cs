using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class InternalEndpoints
{
    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        // 注意: mTLS が有効なら Kestrel 側 ClientCertificateValidation で agent の証明書が認証される。
        // ここでは Authorization は要求せず、mTLS による相互認証に委ねる。
        var group = app.MapGroup("/api/internal");

        group.MapPost("/heartbeat", async (HeartbeatRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AgentId))
                return Results.BadRequest(new { error = "AgentId が必要です。" });
            var node = await db.ExecutionNodes.FirstOrDefaultAsync(n => n.Name == req.AgentId, ct);
            if (node is null) return Results.NotFound(new { error = "Unknown agent" });
            node.LastHeartbeatAt = req.Timestamp == default ? DateTime.UtcNow : req.Timestamp;
            node.HealthStatus = HealthStatuses.Healthy;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/audit-logs/batch", async (AuditBatchRequest body, AppDbContext db, CancellationToken ct) =>
        {
            if (body.Items is null || body.Items.Length == 0) return Results.NoContent();
            int added = 0;
            foreach (var json in body.Items)
            {
                if (string.IsNullOrWhiteSpace(json)) continue;
                try
                {
                    var log = JsonSerializer.Deserialize<AuditLog>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                    if (log is null) continue;
                    log.Id = 0; // 中央側で自動採番
                    if (log.Timestamp == default) log.Timestamp = DateTime.UtcNow;
                    db.AuditLogs.Add(log);
                    added++;
                }
                catch { /* 個別の壊れたレコードはスキップ */ }
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added });
        });

        return app;
    }

    public record HeartbeatRequest(string AgentId, DateTime Timestamp);
    public record AuditBatchRequest(string[] Items);
}
