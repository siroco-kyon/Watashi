using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminLogEndpoints
{
    public static IEndpointRouteBuilder MapAdminLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/logs").RequireAuthorization("Admin");

        group.MapGet("/", async (
            string? user, string? op, string? category, string? result,
            string? host, string? share, string? path, string? node, string? device,
            DateTime? from, DateTime? to, int? page,
            AppDbContext db, CancellationToken ct) =>
        {
            const int PageSize = 100;
            var filter = BuildFilter(user, op, category, result, host, share, path, node, device, from, to, page);
            var q = ApplyFilter(db, filter);
            var total = await q.CountAsync(ct);
            var totalPages = Math.Max(1, (total + PageSize - 1) / PageSize);
            var p = Math.Clamp(page ?? 1, 1, totalPages);

            // 関連マスタへ left-join し、削除済みの場合は ID を残したまま名前だけ null にする。
            var rows = await (
                from l in q.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id)
                    .Skip((p - 1) * PageSize).Take(PageSize)
                join h in db.CifsHosts.AsNoTracking() on l.HostId equals h.Id into hj
                from h in hj.DefaultIfEmpty()
                join s in db.CifsShares.AsNoTracking() on l.ShareId equals s.Id into sj
                from s in sj.DefaultIfEmpty()
                join n in db.ExecutionNodes.AsNoTracking() on l.ExecutionNodeId equals n.Id into nj
                from n in nj.DefaultIfEmpty()
                select new AuditLogDto
                {
                    Id = l.Id,
                    Timestamp = l.Timestamp,
                    UserId = l.UserId,
                    Username = l.Username,
                    Operation = l.Operation,
                    HostId = l.HostId,
                    ShareId = l.ShareId,
                    HostName = h != null ? h.Name : null,
                    ShareName = s != null ? s.DisplayName : null,
                    Path = l.Path,
                    TargetPath = l.TargetPath,
                    Result = l.Result,
                    ErrorMessage = l.ErrorMessage,
                    ClientIp = l.ClientIp,
                    ClientHostname = l.ClientHostname,
                    BytesTransferred = l.BytesTransferred,
                    DurationMs = l.DurationMs,
                    Protocol = l.Protocol,
                    ExecutionNodeId = l.ExecutionNodeId,
                    ExecutionNodeName = n != null ? n.Name : null,
                    UsedPermissionId = l.UsedPermissionId,
                }).ToListAsync(ct);
            return Results.Ok(new AuditLogPageDto
            {
                TotalCount = total,
                Page = p,
                PageSize = PageSize,
                Items = rows,
            });
        });

        group.MapGet("/export.csv", async (
            string? user, string? op, string? category, string? result,
            string? host, string? share, string? path, string? node, string? device,
            DateTime? from, DateTime? to,
            HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var filter = BuildFilter(user, op, category, result, host, share, path, node, device, from, to, page: null);
            var filtered = ApplyFilter(db, filter)
                .OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id);
            var q =
                from l in filtered
                join h in db.CifsHosts.AsNoTracking() on l.HostId equals h.Id into hj
                from h in hj.DefaultIfEmpty()
                join s in db.CifsShares.AsNoTracking() on l.ShareId equals s.Id into sj
                from s in sj.DefaultIfEmpty()
                join n in db.ExecutionNodes.AsNoTracking() on l.ExecutionNodeId equals n.Id into nj
                from n in nj.DefaultIfEmpty()
                select new AuditCsvRow(
                    l.Id, l.Timestamp, l.Username, l.Operation,
                    l.HostId, h != null ? h.Name : null,
                    l.ShareId, s != null ? s.DisplayName : null,
                    l.Path, l.TargetPath, l.Result, l.ErrorMessage,
                    l.ClientIp, l.ClientHostname, l.BytesTransferred, l.DurationMs, l.Protocol,
                    l.ExecutionNodeId, n != null ? n.Name : null, l.UsedPermissionId);

            ctx.Response.ContentType = "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=audit_logs.csv";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetPreamble(), ct);
            await using var writer = new StreamWriter(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
            // 「どこのどの共有のどのパスか」が一目で分かるよう Location 列を追加。
            // 既存運用のために HostId/ShareId/Path/HostName/ShareName 各列もそのまま残す。
            await writer.WriteLineAsync("Id,Timestamp,Username,Operation,OperationLabel,Location,HostId,HostName,ShareId,ShareName,Path,TargetPath,Result,Error,ClientIp,ClientHostname,Bytes,DurationMs,Protocol,NodeId,PermId,OperationCategory,NodeName");

            int batched = 0;
            await foreach (var l in q.AsAsyncEnumerable().WithCancellation(ct))
            {
                var fields = new[]
                {
                    l.Id.ToString(CultureInfo.InvariantCulture),
                    l.Timestamp.ToString("o"),
                    l.Username,
                    l.Operation,
                    AuditLogDto.FormatOperation(l.Operation),
                    AuditLogDto.FormatLocation(l.HostName, l.HostId, l.ShareName, l.ShareId, l.Path),
                    l.HostId?.ToString() ?? string.Empty,
                    l.HostName ?? string.Empty,
                    l.ShareId?.ToString() ?? string.Empty,
                    l.ShareName ?? string.Empty,
                    l.Path ?? string.Empty,
                    l.TargetPath ?? string.Empty,
                    l.Result,
                    l.ErrorMessage ?? string.Empty,
                    l.ClientIp ?? string.Empty,
                    l.ClientHostname ?? string.Empty,
                    l.BytesTransferred?.ToString() ?? string.Empty,
                    l.DurationMs?.ToString() ?? string.Empty,
                    l.Protocol ?? string.Empty,
                    l.ExecutionNodeId?.ToString() ?? string.Empty,
                    l.UsedPermissionId?.ToString() ?? string.Empty,
                    AuditLogFilterValues.CategoryFor(l.Operation),
                    l.ExecutionNodeName ?? string.Empty,
                };
                await writer.WriteLineAsync(string.Join(',', fields.Select(Csv)));
                if (++batched % 500 == 0) await writer.FlushAsync();
            }
            await writer.FlushAsync();
            return Results.Empty;
        });

        return app;
    }

    internal static IQueryable<AuditLog> ApplyFilter(AppDbContext db, AuditLogQueryDto filter)
    {
        var q = db.AuditLogs.AsNoTracking();

        var user = NormalizeText(filter.User);
        if (user is not null)
        {
            var lowered = user.ToLowerInvariant();
            var hasUserId = int.TryParse(user, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId);
            q = q.Where(l => l.Username.ToLower().Contains(lowered) || (hasUserId && l.UserId == userId));
        }

        var operation = NormalizeOperationFilter(filter.Op);
        if (!string.IsNullOrWhiteSpace(operation)) q = q.Where(l => l.Operation == operation);

        var category = NormalizeText(filter.Category)?.ToLowerInvariant();
        var fileOperations = AuditLogFilterValues.FileOperationCodes.ToArray();
        q = category switch
        {
            AuditLogFilterValues.FileCategory => q.Where(l => fileOperations.Contains(l.Operation)),
            AuditLogFilterValues.AuthCategory => q.Where(l =>
                l.Operation.StartsWith("LOGIN_") || l.Operation.StartsWith("PASSWORD_")),
            AuditLogFilterValues.AdminCategory => q.Where(l => l.Operation.StartsWith("ADMIN_")),
            AuditLogFilterValues.OtherCategory => q.Where(l =>
                !fileOperations.Contains(l.Operation) &&
                !l.Operation.StartsWith("LOGIN_") &&
                !l.Operation.StartsWith("PASSWORD_") &&
                !l.Operation.StartsWith("ADMIN_")),
            _ => q,
        };

        var result = NormalizeResultFilter(filter.Result);
        if (result is not null) q = q.Where(l => l.Result == result);

        var host = NormalizeText(filter.Host);
        if (host is not null)
        {
            var lowered = host.ToLowerInvariant();
            var hasId = int.TryParse(host, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostId);
            q = q.Where(l =>
                (hasId && l.HostId == hostId) ||
                db.CifsHosts.Any(h => h.Id == l.HostId &&
                    (h.Name.ToLower().Contains(lowered) || h.HostAddress.ToLower().Contains(lowered))));
        }

        var share = NormalizeText(filter.Share);
        if (share is not null)
        {
            var lowered = share.ToLowerInvariant();
            var hasId = int.TryParse(share, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shareId);
            q = q.Where(l =>
                (hasId && l.ShareId == shareId) ||
                db.CifsShares.Any(s => s.Id == l.ShareId &&
                    (s.DisplayName.ToLower().Contains(lowered) || s.ShareName.ToLower().Contains(lowered))));
        }

        var path = NormalizeText(filter.Path);
        if (path is not null)
        {
            var lowered = path.ToLowerInvariant();
            q = q.Where(l =>
                (l.Path != null && l.Path.ToLower().Contains(lowered)) ||
                (l.TargetPath != null && l.TargetPath.ToLower().Contains(lowered)));
        }

        var node = NormalizeText(filter.Node);
        if (node is not null)
        {
            var lowered = node.ToLowerInvariant();
            var hasId = int.TryParse(node, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nodeId);
            q = q.Where(l =>
                (hasId && l.ExecutionNodeId == nodeId) ||
                db.ExecutionNodes.Any(n => n.Id == l.ExecutionNodeId && n.Name.ToLower().Contains(lowered)));
        }

        var device = NormalizeText(filter.Device);
        if (device is not null)
        {
            var lowered = device.ToLowerInvariant();
            q = q.Where(l =>
                (l.ClientHostname != null && l.ClientHostname.ToLower().Contains(lowered)) ||
                (l.ClientIp != null && l.ClientIp.ToLower().Contains(lowered)));
        }

        if (filter.From.HasValue) q = q.Where(l => l.Timestamp >= filter.From.Value);
        if (filter.To.HasValue) q = q.Where(l => l.Timestamp <= filter.To.Value);
        return q;
    }

    private static AuditLogQueryDto BuildFilter(
        string? user, string? op, string? category, string? result,
        string? host, string? share, string? path, string? node, string? device,
        DateTime? from, DateTime? to, int? page) => new()
        {
            User = user,
            Op = op,
            Category = category,
            Result = result,
            Host = host,
            Share = share,
            Path = path,
            Node = node,
            Device = device,
            From = from,
            To = to,
            Page = page,
        };

    private static string? NormalizeOperationFilter(string? op)
    {
        if (string.IsNullOrWhiteSpace(op)) return null;
        var value = op.Trim();
        var option = AuditLogFilterValues.OperationOptions.FirstOrDefault(x =>
            string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Label, value, StringComparison.OrdinalIgnoreCase));
        return option?.Value ?? value.ToUpperInvariant();
    }

    private static string? NormalizeResultFilter(string? result)
    {
        var value = NormalizeText(result);
        if (value is null) return null;
        return value.ToLowerInvariant() switch
        {
            "成功" => AuditResults.Success,
            "失敗" => AuditResults.Failure,
            "警告" => AuditResults.Warning,
            var normalized => normalized,
        };
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Csv(string s) => CsvHelper.Escape(s);

    private record AuditCsvRow(
        long Id, DateTime Timestamp, string Username, string Operation,
        int? HostId, string? HostName, int? ShareId, string? ShareName,
        string? Path, string? TargetPath, string Result, string? ErrorMessage,
        string? ClientIp, string? ClientHostname, long? BytesTransferred, long? DurationMs, string? Protocol,
        int? ExecutionNodeId, string? ExecutionNodeName, int? UsedPermissionId);
}
