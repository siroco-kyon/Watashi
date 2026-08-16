using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Constants;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

public class AuditLogService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuditLogService>? _logger;

    public AuditLogService(AppDbContext db, ILogger<AuditLogService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(AuditLog log, CancellationToken ct = default)
    {
        if (log.Timestamp == default) log.Timestamp = DateTime.UtcNow;
        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 操作結果を監査保存障害へ連鎖させない。直接保存できなければ同じDBのoutboxへ退避し、
    /// それも失敗した場合は構造化ログへ残す。eventIdにより応答喪失後の再送も重複しない。
    /// </summary>
    public async Task<bool> TryLogWithOutboxAsync(AuditLog log, CancellationToken ct = default)
    {
        if (log.Timestamp == default) log.Timestamp = DateTime.UtcNow;
        log.EventId ??= Guid.NewGuid();
        try
        {
            _db.AuditLogs.Add(log);
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception directError)
        {
            try { _db.Entry(log).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
            catch { }
            try
            {
                var id = log.EventId.Value;
                if (!await _db.AuditOutboxEntries.AnyAsync(e => e.EventId == id, ct))
                {
                    _db.AuditOutboxEntries.Add(new AuditOutboxEntry
                    {
                        EventId = id,
                        PayloadJson = JsonSerializer.Serialize(log),
                        CreatedAt = DateTime.UtcNow,
                        NextAttemptAt = DateTime.UtcNow.AddSeconds(15),
                        AttemptCount = 0,
                        LastError = LimitError(directError.GetBaseException().Message),
                    });
                    await _db.SaveChangesAsync(ct);
                }
                _logger?.LogWarning(directError,
                    "Audit write was queued in outbox for event {EventId} operation {Operation}",
                    id, log.Operation);
                return true;
            }
            catch (Exception outboxError)
            {
                try { _db.ChangeTracker.Clear(); } catch { }
                _logger?.LogError(outboxError,
                    "Audit and outbox writes failed for event {EventId} operation {Operation}; original error: {OriginalError}",
                    log.EventId, log.Operation, directError.GetBaseException().Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 認証処理の成否をベストエフォートで記録する。認証状態の変更を永続化した後に呼び出し、
    /// 監査テーブルだけが利用不能でも、成功済みのログインやトークン失効を失敗応答へ変えない。
    /// reasonCode は定型コードだけを受け付け、例外文や資格情報が誤って保存されるのを防ぐ。
    /// </summary>
    public async Task<bool> TryLogAuthenticationAsync(
        int? userId,
        string? username,
        string operation,
        string result,
        string reasonCode,
        string? clientIp,
        string? clientHostname,
        string? detail = null,
        CancellationToken ct = default)
    {
        var log = new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            UserId = userId,
            Username = string.IsNullOrWhiteSpace(username) ? "(anonymous)" : username.Trim(),
            Operation = operation,
            Result = result,
            ErrorMessage = SafeReasonCode(reasonCode),
            Path = string.IsNullOrWhiteSpace(detail) ? null : detail,
            ClientIp = string.IsNullOrWhiteSpace(clientIp) ? null : clientIp.Trim(),
            ClientHostname = string.IsNullOrWhiteSpace(clientHostname) ? null : clientHostname.Trim(),
        };

        return await TryLogWithOutboxAsync(log, ct);
    }

    private static string SafeReasonCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return "unspecified";

        // 理由欄に保存できるのは lower_snake_case の識別子だけ。パスワード、token、
        // 例外メッセージ、リクエスト本文などの自由入力はこの境界で拒否する。
        foreach (var c in value)
        {
            if ((c is < 'a' or > 'z') && (c is < '0' or > '9') && c != '_')
                return "unspecified";
        }

        return value;
    }

    public async Task LogAsync(
        ClaimsPrincipal principal,
        HttpContext ctx,
        string operation,
        int? hostId,
        int? shareId,
        string? path,
        string result,
        string? errorMessage = null,
        string? targetPath = null,
        long? bytesTransferred = null,
        long? durationMs = null,
        int? executionNodeId = null,
        int? usedPermissionId = null,
        CancellationToken ct = default)
    {
        int? userId = principal.GetUserId();
        var username = principal.GetUsername() ?? "(anonymous)";
        await TryLogWithOutboxAsync(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            UserId = userId,
            Username = username,
            Operation = operation,
            HostId = hostId,
            ShareId = shareId,
            Path = path,
            TargetPath = targetPath,
            Result = result,
            ErrorMessage = errorMessage,
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ClientHostname = ctx.Request.Headers["X-Client-Hostname"].FirstOrDefault(),
            BytesTransferred = bytesTransferred,
            DurationMs = durationMs,
            Protocol = ctx.Request.IsHttps ? "HTTPS" : "HTTP",
            ExecutionNodeId = executionNodeId,
            UsedPermissionId = usedPermissionId,
        }, ct);
    }

    private static string LimitError(string value) => value.Length <= 1000 ? value : value[..1000];

    /// <summary>
    /// 管理者操作の監査ログを記録する軽量ヘルパー。target は "user:42" のような識別子。
    /// </summary>
    public Task LogAdminAsync(
        ClaimsPrincipal principal,
        HttpContext ctx,
        string operation,
        string? target,
        string result = AuditResults.Success,
        string? errorMessage = null,
        CancellationToken ct = default)
        => LogAsync(principal, ctx, operation, hostId: null, shareId: null, path: target,
            result: result, errorMessage: errorMessage, ct: ct);
}
