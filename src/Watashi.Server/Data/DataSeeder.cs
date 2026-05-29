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
                new SystemSetting { Key = SettingKeys.PasswordExpiryDays, Value = "90", UpdatedAt = now },
                new SystemSetting { Key = SettingKeys.PasswordWarningDays, Value = "14", UpdatedAt = now },
                new SystemSetting { Key = SettingKeys.AgentMaxConcurrency, Value = "20", UpdatedAt = now },
                new SystemSetting { Key = SettingKeys.SessionIdleMinutes, Value = "30", UpdatedAt = now },
                new SystemSetting { Key = SettingKeys.AuditLogRetentionDays, Value = "365", UpdatedAt = now },
                new SystemSetting { Key = SettingKeys.MaxFailedLoginAttempts, Value = "15", UpdatedAt = now });
        }
        else
        {
            // 既存 DB に無い設定を補完
            if (!await db.SystemSettings.AnyAsync(s => s.Key == SettingKeys.AuditLogRetentionDays, ct))
                db.SystemSettings.Add(new SystemSetting { Key = SettingKeys.AuditLogRetentionDays, Value = "365", UpdatedAt = now });
            if (!await db.SystemSettings.AnyAsync(s => s.Key == SettingKeys.MaxFailedLoginAttempts, ct))
                db.SystemSettings.Add(new SystemSetting { Key = SettingKeys.MaxFailedLoginAttempts, Value = "15", UpdatedAt = now });
        }

        if (!await db.PermissionTemplates.AnyAsync(ct))
        {
            // 新規 DB ではフルアクセスを Id=1 に。新規ユーザー権限作成時のデフォルト選択。
            db.PermissionTemplates.AddRange(
                new PermissionTemplate { Name = "フルアクセス", CanRead = true, CanWrite = true, CanDelete = true, CanRename = true },
                new PermissionTemplate { Name = "読取+書込", CanRead = true, CanWrite = true },
                new PermissionTemplate { Name = "読取のみ", CanRead = true });
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
