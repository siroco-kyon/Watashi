using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Server.Endpoints;

public static class AdminDeviceEndpoints
{
    public static IEndpointRouteBuilder MapAdminDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/devices").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var devices = await (
                from d in db.TrustedDevices.AsNoTracking()
                join u in db.Users.AsNoTracking() on d.UserId equals u.Id
                orderby u.Username, d.MachineName
                select new DeviceDto
                {
                    Id = d.Id,
                    UserId = d.UserId,
                    Username = u.Username,
                    MachineName = d.MachineName,
                    WindowsUsername = d.WindowsUsername,
                    RegisteredAt = d.RegisteredAt,
                    LastUsedAt = d.LastUsedAt,
                    IsRevoked = d.IsRevoked,
                    RevokedReason = d.RevokedReason,
                    RevokedAt = d.RevokedAt,
                }).ToListAsync(ct);

            return Results.Ok(devices);
        });

        return app;
    }
}
