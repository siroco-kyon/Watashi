using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/settings").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.SystemSettings.Select(s => new { s.Key, s.Value, s.UpdatedAt }).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapGet("/{key}", async (string key, AppDbContext db, CancellationToken ct) =>
        {
            var s = await db.SystemSettings.FindAsync(new object?[] { key }, ct);
            return s is null ? Results.NotFound() : Results.Ok(new { s.Key, s.Value, s.UpdatedAt });
        });

        group.MapPut("/{key}", async (string key, SettingUpdate body, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            int? uid = int.TryParse(principal.FindFirst("uid")?.Value, out var u) ? u : null;
            var s = await db.SystemSettings.FindAsync(new object?[] { key }, ct);
            var now = DateTime.UtcNow;
            if (s is null)
            {
                db.SystemSettings.Add(new SystemSetting { Key = key, Value = body.Value ?? string.Empty, UpdatedAt = now, UpdatedBy = uid });
            }
            else
            {
                s.Value = body.Value ?? string.Empty;
                s.UpdatedAt = now;
                s.UpdatedBy = uid;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    public record SettingUpdate(string? Value);
}
