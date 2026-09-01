using System.Security.Claims;
using System.Text.Json;
using System.Security.Cryptography;
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

        group.MapPost("/heartbeat", async (HeartbeatRequest req, HttpContext ctx, ClaimsPrincipal principal, AppDbContext db, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Internal");
            if (string.IsNullOrWhiteSpace(req.AgentId))
                return Results.BadRequest(new { error = "AgentId が必要です。" });
            var headerAgentId = ReadClaimedAgentId(ctx.Request);
            if (ctx.Request.Headers.ContainsKey(AgentProtocolHeaders.AgentId) && headerAgentId is null)
                return Results.BadRequest(new { error = $"{AgentProtocolHeaders.AgentId} は1つの非空値で指定してください。" });
            if (headerAgentId is not null &&
                !string.Equals(headerAgentId, req.AgentId, StringComparison.Ordinal))
            {
                logger.LogWarning("Heartbeat AgentId 不一致 header={Header} body={Body}", headerAgentId, req.AgentId);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var node = await ResolveHeartbeatNodeAsync(principal, db, req.AgentId, ct);
            if (node is null)
            {
                logger.LogWarning("Heartbeat の認証主体を Agent に結び付けられません。AgentId={AgentId}", req.AgentId);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (!string.Equals(node.Name, req.AgentId, StringComparison.Ordinal))
            {
                logger.LogWarning("Heartbeat AgentId 不一致 authenticated={Authenticated} body={Body}", node.Name, req.AgentId);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
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

        group.MapPost("/audit-logs/batch", async (AuditBatchRequest body, HttpContext ctx, ClaimsPrincipal principal, AppDbContext db, ILoggerFactory lf, CancellationToken ct) =>
        {
            var logger = lf.CreateLogger("Internal");
            if (body.Items is null || body.Items.Length == 0) return Results.NoContent();
            var certificateNode = await ResolveCertificateNodeAsync(principal, db, ct);
            var usesSharedSecret = principal.HasClaim(AgentOrSharedSecretHandler.SharedSecretClaim, "1");
            if (certificateNode is null && !usesSharedSecret)
            {
                logger.LogWarning("監査ログ送信主体を認証できません。");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var headerAgentId = ReadClaimedAgentId(ctx.Request);
            if (ctx.Request.Headers.ContainsKey(AgentProtocolHeaders.AgentId) && headerAgentId is null)
                return Results.BadRequest(new { error = $"{AgentProtocolHeaders.AgentId} は1つの非空値で指定してください。" });

            ExecutionNode? authenticatedNode = certificateNode;
            if (certificateNode is not null && headerAgentId is not null &&
                !string.Equals(certificateNode.Name, headerAgentId, StringComparison.Ordinal))
            {
                logger.LogWarning("監査ログ AgentId 不一致 authenticated={Authenticated} header={Header}",
                    certificateNode.Name, headerAgentId);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (authenticatedNode is null && headerAgentId is not null)
            {
                authenticatedNode = await ResolveSharedSecretNodeAsync(
                    principal, db, headerAgentId, ct);
                if (authenticatedNode is null)
                {
                    logger.LogWarning("監査ログの AgentId を有効なノードに結び付けられません。AgentId={AgentId}",
                        headerAgentId);
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }
            var agentLabel = authenticatedNode?.Name ?? "shared-secret";
            var accepted = new List<int>();
            var rejected = new List<AuditRejectedItem>();
            var candidates = new List<(int Index, AuditLog Log)>();
            for (var index = 0; index < body.Items.Length; index++)
            {
                var json = body.Items[index];
                if (string.IsNullOrWhiteSpace(json))
                {
                    rejected.Add(new AuditRejectedItem(index, "empty_payload"));
                    continue;
                }
                try
                {
                    var log = TryParseAuditLog(json);
                    if (log is null)
                    {
                        rejected.Add(new AuditRejectedItem(index, "invalid_payload"));
                        logger.LogWarning("audit-log バッチ内の不正レコードをスキップ agent={Agent} len={Len}", agentLabel, json.Length);
                        continue;
                    }
                    if (authenticatedNode is not null)
                        BindAuthenticatedAgent(log, authenticatedNode.Id, authenticatedNode.Name, DateTime.UtcNow);
                    else
                        BindSharedSecretAgent(log, DateTime.UtcNow);
                    candidates.Add((index, log));
                }
                catch (Exception ex)
                {
                    rejected.Add(new AuditRejectedItem(index, "invalid_payload"));
                    logger.LogWarning(ex, "audit-log バッチ内のレコード解析に失敗 agent={Agent} len={Len}", agentLabel, json.Length);
                }
            }

            var eventIds = candidates.Select(x => x.Log.EventId!.Value).Distinct().ToArray();
            var existing = (await db.AuditLogs.AsNoTracking()
                    .Where(x => x.EventId.HasValue && eventIds.Contains(x.EventId.Value))
                    .Select(x => x.EventId!.Value)
                    .ToListAsync(ct))
                .ToHashSet();
            foreach (var groupByEvent in candidates.GroupBy(x => x.Log.EventId!.Value))
            {
                var first = groupByEvent.First();
                if (!existing.Contains(groupByEvent.Key))
                    db.AuditLogs.Add(first.Log);
                // 同じeventの再送や同一batch内重複も保存済み扱いで個別ACKする。
                accepted.AddRange(groupByEvent.Select(x => x.Index));
            }
            await db.SaveChangesAsync(ct);
            if (rejected.Count > 0)
                logger.LogWarning("audit-log バッチ: agent={Agent} accepted={Accepted} rejected={Rejected}",
                    agentLabel, accepted.Count, rejected.Count);
            return Results.Ok(new AuditBatchAck(accepted.ToArray(), rejected.ToArray()));
        });

        return app;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    internal static string? ReadClaimedAgentId(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(AgentProtocolHeaders.AgentId, out var values) ||
            values.Count != 1)
            return null;
        var value = values[0]?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// mTLS は証明書に対応する node を使用する。共有秘密の場合は従来互換として、
    /// heartbeat 本文の AgentId を有効な Agent node と照合する。
    /// </summary>
    internal static async Task<ExecutionNode?> ResolveHeartbeatNodeAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        string claimedAgentId,
        CancellationToken ct = default)
    {
        var certificateNode = await ResolveCertificateNodeAsync(principal, db, ct);
        if (certificateNode is not null) return certificateNode;

        return await ResolveSharedSecretNodeAsync(principal, db, claimedAgentId, ct);
    }

    internal static async Task<ExecutionNode?> ResolveSharedSecretNodeAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        string claimedAgentId,
        CancellationToken ct = default)
    {
        if (!principal.HasClaim(AgentOrSharedSecretHandler.SharedSecretClaim, "1") ||
            string.IsNullOrWhiteSpace(claimedAgentId))
            return null;
        var normalizedAgentId = claimedAgentId.Trim();
        return await db.ExecutionNodes.FirstOrDefaultAsync(
            node => node.Name == normalizedAgentId &&
                    node.IsActive &&
                    node.NodeType == NodeTypes.Agent,
            ct);
    }

    internal static async Task<ExecutionNode?> ResolveCertificateNodeAsync(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken ct = default)
    {
        var nodeIdValue = principal.FindFirst(AgentCertificateValidator.NodeIdClaim)?.Value;
        if (!int.TryParse(nodeIdValue, out var nodeId)) return null;
        return await db.ExecutionNodes.FirstOrDefaultAsync(
            node => node.Id == nodeId && node.IsActive && node.NodeType == NodeTypes.Agent, ct);
    }

    internal static void BindAuthenticatedAgent(
        AuditLog log, int nodeId, string nodeName, DateTime receivedAt)
    {
        // Agent 申告値を中央サーバ自身のユーザー監査と同じ主体・操作として保存すると、
        // Agent credentialの保持者が任意ユーザーの管理/ファイル操作を偽装できる。
        // 認証済みAgentを主体に固定し、申告操作は明示的な名前空間へ分離する。
        var reportedOperation = log.Operation.Trim();
        if (reportedOperation.Length > 128) reportedOperation = reportedOperation[..128];
        log.UserId = null;
        log.Username = $"(agent:{nodeName})";
        log.Operation = $"AGENT_REPORTED/{reportedOperation}";
        log.ExecutionNodeId = nodeId;
        log.Timestamp = receivedAt;
        log.Protocol = "AGENT";
    }

    internal static void BindSharedSecretAgent(AuditLog log, DateTime receivedAt)
    {
        // 共通の共有秘密では複数 Agent のうちどれが送信したかを証明できない。
        // 受理はするが、申告されたユーザー/ノードを信頼済み主体として保存しない。
        var reportedOperation = log.Operation.Trim();
        if (reportedOperation.Length > 128) reportedOperation = reportedOperation[..128];
        log.UserId = null;
        log.Username = "(agent:shared-secret)";
        log.Operation = $"AGENT_REPORTED/{reportedOperation}";
        log.ExecutionNodeId = null;
        log.Timestamp = receivedAt;
        log.Protocol = "AGENT";
    }

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
        log.EventId ??= DeterministicEventId(json);
        if (string.IsNullOrWhiteSpace(log.Operation)) return null;
        if (string.IsNullOrWhiteSpace(log.Username)) log.Username = "(agent)";
        var result = log.Result?.Trim().ToLowerInvariant();
        if (result is not (AuditResults.Success or AuditResults.Failure or AuditResults.Warning)) return null;
        log.Result = result;
        return log;
    }

    private static Guid DeterministicEventId(string json)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Agent からのハートビート。Timestamp は旧 Agent との互換のため受け取るだけで、
    /// サーバ側では使用しない (Agent の時計ずれ対策として受信時刻を採用する)。
    /// </summary>
    public record HeartbeatRequest(string AgentId, DateTime Timestamp);
    public record AuditBatchRequest(string[] Items);
    public record AuditRejectedItem(int Index, string Error);
    public record AuditBatchAck(int[] Accepted, AuditRejectedItem[] Rejected);
    public class HeartbeatAck
    {
        /// <summary>管理画面で設定された Node.MaxConcurrency。Agent はこの値で同時実行制限を更新する。</summary>
        public int MaxConcurrency { get; set; }
    }
}
