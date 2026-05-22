using Microsoft.EntityFrameworkCore;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Server.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (!await db.SystemSettings.AnyAsync(ct))
        {
            db.SystemSettings.AddRange(
                new SystemSetting { Key = "PasswordExpiryDays", Value = "90", UpdatedAt = now },
                new SystemSetting { Key = "PasswordWarningDays", Value = "14", UpdatedAt = now },
                new SystemSetting { Key = "AgentMaxConcurrency", Value = "20", UpdatedAt = now },
                new SystemSetting { Key = "SessionIdleMinutes", Value = "30", UpdatedAt = now });
        }

        if (!await db.PermissionTemplates.AnyAsync(ct))
        {
            db.PermissionTemplates.AddRange(
                new PermissionTemplate { Name = "読取のみ", CanRead = true },
                new PermissionTemplate { Name = "読取+書込", CanRead = true, CanWrite = true },
                new PermissionTemplate { Name = "フルアクセス", CanRead = true, CanWrite = true, CanDelete = true, CanRename = true });
        }

        if (!await db.ExecutionNodes.AnyAsync(ct))
        {
            db.ExecutionNodes.Add(new ExecutionNode
            {
                Name = "Direct (Local)",
                NodeType = NodeTypes.Direct,
                HealthStatus = HealthStatuses.Healthy,
                IsActive = true,
                MaxConcurrency = 20,
                CreatedAt = now,
            });
        }

        if (!await db.Users.AnyAsync(ct))
        {
            db.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!@#"),
                IsAdmin = true,
                MustChangePassword = true,
                PasswordChangedAt = now,
                PasswordExpiresAt = now.AddDays(90),
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
