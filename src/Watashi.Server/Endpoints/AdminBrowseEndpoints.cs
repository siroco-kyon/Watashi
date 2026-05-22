using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Server.Services.Cifs;
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
            CifsService cifs,
            EncryptionService enc,
            CancellationToken ct) =>
        {
            var row = await (from h in db.CifsHosts
                join s in db.CifsShares on h.Id equals s.HostId
                where h.Id == hostId && s.Id == shareId
                select new { h.HostAddress, h.Port, h.CredUsername, h.CredPasswordEnc, s.ShareName }
            ).FirstOrDefaultAsync(ct);
            if (row is null) return Results.BadRequest(new { error = "ホスト/共有が見つかりません。" });
            var pw = enc.Decrypt(row.CredPasswordEnc);
            var info = new CifsConnectionInfo(row.HostAddress, row.Port, row.CredUsername, pw, row.ShareName);
            var normalized = PathHelper.NormalizePath(path);
            var list = await Task.Run(() => cifs.List(info, normalized), ct);
            return Results.Ok(new { currentPath = normalized, entries = list });
        }).RequireAuthorization("Admin");

        return app;
    }
}
