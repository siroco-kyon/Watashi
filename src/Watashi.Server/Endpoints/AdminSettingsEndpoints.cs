using System.Security.Claims;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/settings").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.SystemSettings.AsNoTracking()
                .Select(s => new { s.Key, s.Value, s.UpdatedAt })
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapGet("/{key}", async (string key, AppDbContext db, CancellationToken ct) =>
        {
            var s = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
            return s is null ? Results.NotFound() : Results.Ok(new { s.Key, s.Value, s.UpdatedAt });
        });

        group.MapPut("/{key}", async (string key, SettingUpdate body, AppDbContext db, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var validationError = ValidateSetting(key, body.Value);
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            int? uid = principal.GetUserId();
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
            await audit.LogAdminAsync(principal, ctx, AdminOperations.SettingUpdate, $"setting:{key}", ct: ct);
            return Results.NoContent();
        });

        return app;
    }

    public record SettingUpdate(string? Value);

    internal static string? ValidateSetting(string key, string? value)
    {
        if (key == SettingKeys.TrashCapacityBytes)
        {
            const long maxCapacityBytes = 1024L * 1024 * 1024 * 1024 * 1024; // 1 PiB
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
                return $"{key} は整数で指定してください。";
            if (capacity < 0 || capacity > maxCapacityBytes)
                return $"{key} は 0 以上 {maxCapacityBytes} 以下で指定してください。";
            return null;
        }

        var range = key switch
        {
            SettingKeys.PasswordExpiryDays => (Min: 1, Max: 36_500),
            SettingKeys.PasswordWarningDays => (Min: 0, Max: 36_500),
            SettingKeys.AgentMaxConcurrency => (Min: 1, Max: 100_000),
            SettingKeys.SessionIdleMinutes => (Min: 1, Max: 525_600),
            SettingKeys.AuditLogRetentionDays => (Min: 0, Max: 365_000),
            SettingKeys.MaxFailedLoginAttempts => (Min: 1, Max: 100_000),
            SettingKeys.PasswordSetupExpiryDays => (Min: 0, Max: 36_500),
            SettingKeys.TrashRetentionDays => (Min: 1, Max: 3_650),
            SettingKeys.TrustedDeviceLimit => (Min: 1, Max: 20),
            _ => ((int Min, int Max)?)null,
        };

        if (range is null)
            return $"未対応の設定キーです: {key}";
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return $"{key} は整数で指定してください。";
        if (parsed < range.Value.Min || parsed > range.Value.Max)
            return $"{key} は {range.Value.Min} 以上 {range.Value.Max} 以下で指定してください。";
        return null;
    }
}
