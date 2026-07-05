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
            if (!string.IsNullOrEmpty(certAgent) && !string.Equals(certAgent, req.AgentId, StringComparison.Ordinal))
            {
                logger.LogWarning("Heartbeat AgentId 不一致 cert={Cert} body={Body}", certAgent, req.AgentId);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var node = await db.ExecutionNodes.FirstOrDefaultAsync(n => n.Name == req.AgentId, ct);
            if (node is null) return Results.NotFound(new { error = "Unknown agent" });
            // Agent 申告の Timestamp は使わない。Agent 側の時計が 90 秒以上ずれていると、
            // NodeHealthMonitor (サーバ時計基準) が生存中のノードを Unhealthy 判定し続ける
            // (逆方向のずれなら停止したノードが Healthy のまま残る) ため、受信時刻で記録する。
            node.LastHeartbeatAt = DateTime.UtcNow;
            node.HealthStatus = HealthStatuses.Healthy;
            await db.SaveChangesAsync(ct);
            // 管理画面で更新された Node.MaxConcurrency を Agent に伝える。
            // Agent 側 (HeartbeatService) はこの値で ConcurrencyLimiter.SetMax を呼び、実制限に反映する。
            return Results.Ok(new HeartbeatAck { MaxConcurrency = node.MaxConcurrency });
        });

        group.MapPost("/audit-logs/batch", async (AuditBatchRequest body, ClaimsPrincipal principal, AppDbContext db, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Internal");
            if (body.Items is null || body.Items.Length == 0) return Results.NoContent();
            var certAgent = principal.FindFirst(AgentCertificateValidator.AgentIdClaim)?.Value;
            var agentLabel = certAgent ?? "shared-secret";
            int added = 0, skipped = 0;
            foreach (var json in body.Items)
            {
                if (string.IsNullOrWhiteSpace(json)) continue;
                try
                {
                    var log = TryParseAuditLog(json);
                    if (log is null)
                    {
                        skipped++;
                        logger.LogWarning("audit-log バッチ内の不正レコードをスキップ agent={Agent} len={Len}", agentLabel, json.Length);
                        continue;
                    }
                    db.AuditLogs.Add(log);
                    added++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    logger.LogWarning(ex, "audit-log バッチ内のレコード解析に失敗 agent={Agent} len={Len}", agentLabel, json.Length);
                }
            }
            await db.SaveChangesAsync(ct);
            if (skipped > 0)
                logger.LogWarning("audit-log バッチ: agent={Agent} added={Added} skipped={Skipped}", agentLabel, added, skipped);
            return Results.Ok(new { added, skipped });
        });

        return app;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Agent から届いた監査ログ JSON を検証・正規化する。DB 制約 (Result の CHECK、
    /// Username/Operation の NOT NULL) に違反するレコードが 1 件でも混ざると、バッチ全体の
    /// SaveChanges が失敗 → Agent が再送を繰り返して正常なログまで破棄される (ポイズンバッチ)。
    /// ここで不正レコードを弾いて残りを確実に保存する。不正なら null を返す。
    /// </summary>
    internal static AuditLog? TryParseAuditLog(string json)
    {
        var log = JsonSerializer.Deserialize<AuditLog>(json, JsonOpts);
        if (log is null) return null;
        log.Id = 0;
        if (log.Timestamp == default) log.Timestamp = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(log.Operation)) return null;
        if (string.IsNullOrWhiteSpace(log.Username)) log.Username = "(agent)";
        var result = log.Result?.Trim().ToLowerInvariant();
        if (result is not (AuditResults.Success or AuditResults.Failure or AuditResults.Warning)) return null;
        log.Result = result;
        return log;
    }

    /// <summary>
    /// Agent からのハートビート。Timestamp は旧 Agent との互換のため受け取るだけで、
    /// サーバ側では使用しない (Agent の時計ずれ対策として受信時刻を採用する)。
    /// </summary>
    public record HeartbeatRequest(string AgentId, DateTime Timestamp);
    public record AuditBatchRequest(string[] Items);
    public class HeartbeatAck
    {
        /// <summary>管理画面で設定された Node.MaxConcurrency。Agent はこの値で同時実行制限を更新する。</summary>
        public int MaxConcurrency { get; set; }
    }
}
