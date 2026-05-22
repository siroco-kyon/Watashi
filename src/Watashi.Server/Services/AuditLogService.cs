using System.Security.Claims;
using Watashi.Server.Data;
using Watashi.Shared.Constants;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

public class AuditLogService
{
    private readonly AppDbContext _db;

    public AuditLogService(AppDbContext db) => _db = db;

    public async Task LogAsync(AuditLog log, CancellationToken ct = default)
    {
        if (log.Timestamp == default) log.Timestamp = DateTime.UtcNow;
        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(ct);
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
        await LogAsync(new AuditLog
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
