using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.Users.Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                IsAdmin = u.IsAdmin,
                IsLocked = u.IsLocked,
                LastLoginAt = u.LastLoginAt,
                PasswordExpiresAt = u.PasswordExpiresAt,
                MustChangePassword = u.MustChangePassword,
                CreatedAt = u.CreatedAt,
            }).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/", async (CreateUserRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Username/Password は必須です。" });
            var (ok, err) = PasswordPolicy.Validate(req.Password);
            if (!ok) return Results.BadRequest(new { error = err });
            if (await db.Users.AnyAsync(u => u.Username == req.Username, ct))
                return Results.BadRequest(new { error = "同名ユーザーが既に存在します。" });
            var days = await GetExpiryDaysAsync(db, ct);
            var now = DateTime.UtcNow;
            var u = new User
            {
                Username = req.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                IsAdmin = req.IsAdmin,
                PasswordChangedAt = now,
                PasswordExpiresAt = now.AddDays(days),
                MustChangePassword = true,
                CreatedAt = now,
            };
            db.Users.Add(u);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/users/{u.Id}", new { id = u.Id });
        });

        group.MapPatch("/{id:int}", async (int id, UpdateUserRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();
            if (req.IsAdmin.HasValue) u.IsAdmin = req.IsAdmin.Value;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();
            db.Users.Remove(u);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/unlock", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();
            u.IsLocked = false;
            u.FailedLoginCount = 0;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/reset-password", async (int id, ResetPasswordRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();
            var (ok, err) = PasswordPolicy.Validate(req.NewPassword);
            if (!ok) return Results.BadRequest(new { error = err });
            var days = await GetExpiryDaysAsync(db, ct);
            var now = DateTime.UtcNow;
            u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
            u.PasswordChangedAt = now;
            u.PasswordExpiresAt = now.AddDays(days);
            u.MustChangePassword = true;
            u.FailedLoginCount = 0;
            u.IsLocked = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/devices", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var devices = await db.TrustedDevices.Where(d => d.UserId == id)
                .Select(d => new DeviceDto
                {
                    Id = d.Id,
                    UserId = d.UserId,
                    MachineName = d.MachineName,
                    WindowsUsername = d.WindowsUsername,
                    RegisteredAt = d.RegisteredAt,
                    LastUsedAt = d.LastUsedAt,
                    IsRevoked = d.IsRevoked,
                    RevokedReason = d.RevokedReason,
                    RevokedAt = d.RevokedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(devices);
        });

        group.MapDelete("/{id:int}/devices", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var devices = db.TrustedDevices.Where(d => d.UserId == id);
            var now = DateTime.UtcNow;
            await foreach (var d in devices.AsAsyncEnumerable().WithCancellation(ct))
            {
                d.IsRevoked = true;
                d.RevokedAt = now;
                d.RevokedReason = "admin_revoked";
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task<int> GetExpiryDaysAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == "PasswordExpiryDays", ct);
        return s is not null && int.TryParse(s.Value, out var d) ? d : 90;
    }
}
