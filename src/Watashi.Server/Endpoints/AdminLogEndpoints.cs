using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Server.Endpoints;

public static class AdminLogEndpoints
{
    public static IEndpointRouteBuilder MapAdminLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/logs").RequireAuthorization("Admin");

        group.MapGet("/", async (
            string? user,
            string? op,
            DateTime? from,
            DateTime? to,
            int? page,
            AppDbContext db,
            CancellationToken ct) =>
        {
            const int PageSize = 100;
            int p = Math.Max(1, page ?? 1);
            var q = db.AuditLogs.AsQueryable();
            if (!string.IsNullOrWhiteSpace(user)) q = q.Where(l => l.Username == user);
            if (!string.IsNullOrWhiteSpace(op)) q = q.Where(l => l.Operation == op);
            if (from.HasValue) q = q.Where(l => l.Timestamp >= from.Value);
            if (to.HasValue) q = q.Where(l => l.Timestamp <= to.Value);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(l => l.Timestamp)
                .Skip((p - 1) * PageSize).Take(PageSize)
                .Select(l => new AuditLogDto
                {
                    Id = l.Id, Timestamp = l.Timestamp, UserId = l.UserId, Username = l.Username,
                    Operation = l.Operation, HostId = l.HostId, ShareId = l.ShareId, Path = l.Path,
                    TargetPath = l.TargetPath, Result = l.Result, ErrorMessage = l.ErrorMessage,
                    ClientIp = l.ClientIp, ClientHostname = l.ClientHostname,
                    BytesTransferred = l.BytesTransferred, DurationMs = l.DurationMs,
                    Protocol = l.Protocol, ExecutionNodeId = l.ExecutionNodeId, UsedPermissionId = l.UsedPermissionId,
                })
                .ToListAsync(ct);
            return Results.Ok(new { totalCount = total, page = p, pageSize = PageSize, items = rows });
        });

        group.MapGet("/export.csv", async (
            string? user, string? op, DateTime? from, DateTime? to,
            HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.AuditLogs.AsQueryable();
            if (!string.IsNullOrWhiteSpace(user)) q = q.Where(l => l.Username == user);
            if (!string.IsNullOrWhiteSpace(op)) q = q.Where(l => l.Operation == op);
            if (from.HasValue) q = q.Where(l => l.Timestamp >= from.Value);
            if (to.HasValue) q = q.Where(l => l.Timestamp <= to.Value);

            ctx.Response.ContentType = "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=audit_logs.csv";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetPreamble(), ct);
            await using var writer = new StreamWriter(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync("Id,Timestamp,Username,Operation,HostId,ShareId,Path,TargetPath,Result,Error,ClientIp,Bytes,DurationMs,Protocol,NodeId,PermId");
            await foreach (var l in q.OrderByDescending(l => l.Timestamp).AsAsyncEnumerable().WithCancellation(ct))
            {
                var fields = new[]
                {
                    l.Id.ToString(CultureInfo.InvariantCulture),
                    l.Timestamp.ToString("o"),
                    l.Username,
                    l.Operation,
                    l.HostId?.ToString() ?? string.Empty,
                    l.ShareId?.ToString() ?? string.Empty,
                    l.Path ?? string.Empty,
                    l.TargetPath ?? string.Empty,
                    l.Result,
                    l.ErrorMessage ?? string.Empty,
                    l.ClientIp ?? string.Empty,
                    l.BytesTransferred?.ToString() ?? string.Empty,
                    l.DurationMs?.ToString() ?? string.Empty,
                    l.Protocol ?? string.Empty,
                    l.ExecutionNodeId?.ToString() ?? string.Empty,
                    l.UsedPermissionId?.ToString() ?? string.Empty,
                };
                await writer.WriteLineAsync(string.Join(',', fields.Select(Csv)));
            }
            await writer.FlushAsync();
            return Results.Empty;
        });

        return app;
    }

    private static string Csv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
