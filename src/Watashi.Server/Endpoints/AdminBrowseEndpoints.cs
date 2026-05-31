using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Endpoints;

public static class AdminBrowseEndpoints
{
    public static IEndpointRouteBuilder MapAdminBrowseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/browse", async (
            int hostId,
            int shareId,
            string? path,
            AppDbContext db,
            NodeRouter router,
            EncryptionService enc,
            CancellationToken ct) =>
        {
            var row = await (from h in db.CifsHosts.AsNoTracking()
                join s in db.CifsShares on h.Id equals s.HostId
                join n in db.ExecutionNodes.Include(x => x.GatewayNode) on h.ExecutionNodeId equals n.Id
                where h.Id == hostId && s.Id == shareId
                select new { Host = h, Share = s, Node = n }
            ).FirstOrDefaultAsync(ct);
            if (row is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });
            var pw = enc.Decrypt(row.Host.CredPasswordEnc);
            var info = new CifsConnectionInfo(row.Host.HostAddress, row.Host.Port, row.Host.CredUsername, pw, row.Share.ShareName);
            var normalized = PathHelper.NormalizePath(path);
            try
            {
                var list = await router.ListAsync(row.Node, info, normalized, ct);
                return Results.Ok(new { currentPath = normalized, entries = list });
            }
            catch (Exception ex)
            {
                return FileEndpoints.MapExecutionError(ex);
            }
        }).RequireAuthorization("Admin");

        return app;
    }
}
