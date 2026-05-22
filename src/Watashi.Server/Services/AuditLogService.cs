using System.Security.Claims;
using Watashi.Server.Data;
using Watashi.Shared.Constants;
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
        int? userId = int.TryParse(principal.FindFirst("uid")?.Value, out var uid) ? uid : null;
        var username = principal.FindFirst("name")?.Value ?? "(anonymous)";
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
}
