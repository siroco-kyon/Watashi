using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watashi.Server.Data;
using Xunit;

namespace Watashi.Tests;

/// <summary>
/// AddPasswordSetupPending migration が既存データを一切壊さないことを検証する。
///
/// 背景: Users は UserPermissions / RefreshTokens / TrustedDevices から
/// ON DELETE CASCADE で参照されている。SQLite で列の NOT NULL 制約を変更すると
/// EF はテーブル再構築 (DROP TABLE Users を含む) を行うため、
/// SqlitePragmaInterceptor が有効にしている PRAGMA foreign_keys = ON と重なると
/// 子テーブルが巻き添えで消えうる。この migration を「列追加のみ」に保つことが
/// 安全性の前提になっているので、それをテストで固定する。
/// </summary>
public class PasswordSetupMigrationTests
{
    /// <summary>AddPasswordSetupPending の 1 つ前の migration。</summary>
    private const string PreviousMigration = "AddLoginAuditAndIdentityTracking";

    private static AppDbContext CreateContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True;")
            // 本番と同じ PRAGMA (foreign_keys = ON) を効かせた状態で検証する。
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Migration_preserves_existing_users_and_their_cascade_children()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"watashi-migration-{Guid.NewGuid():N}.db");
        try
        {
            // --- 1. 1 つ前の migration まで適用した「既存 DB」を作る ---
            await using (var db = CreateContext(dbPath))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

                // このテストが空回りしていないことの確認: この時点では新しい列がまだ無い。
                var columns = await db.Database
                    .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Users')")
                    .ToListAsync();
                columns.Should().NotContain("IsPasswordSetupPending");
                columns.Should().NotContain("IsDisabled");

                var permissionColumns = await db.Database
                    .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('UserPermissions')")
                    .ToListAsync();
                permissionColumns.Should().NotContain("ValidFrom");
                permissionColumns.Should().NotContain("ExpiresAt");

                await SeedLegacyDataAsync(db);
            }

            // --- 2. 新しい migration を適用する ---
            await using (var db = CreateContext(dbPath))
            {
                await db.Database.MigrateAsync();

                // --- 3. 既存データが完全に残っていること ---
                var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
                users.Should().HaveCount(2);
                users.Select(u => u.Username).Should().Equal("alice", "bob");
                // パスワードハッシュが変質していない = 既存ユーザーは今までどおりログインできる。
                users[0].PasswordHash.Should().Be("HASH-ALICE");
                users[1].PasswordHash.Should().Be("HASH-BOB");
                users[0].IsAdmin.Should().BeTrue();
                // 最新 migration まで適用した列は Windows アカウント名と同じく
                // 大文字・小文字を区別せず検索できる。
                (await db.Users.AsNoTracking().SingleAsync(u => u.Username == "ALICE"))
                    .Username.Should().Be("alice");

                // Cascade 参照している子テーブルが巻き添えで消えていないこと。
                (await db.UserPermissions.CountAsync()).Should().Be(2);
                var permissions = await db.UserPermissions.AsNoTracking().ToListAsync();
                permissions.Should().OnlyContain(p => p.ValidFrom == null && p.ExpiresAt == null);
                permissions.Should().OnlyContain(p => p.Reason == null && p.TicketNumber == null);
                (await db.RefreshTokens.CountAsync()).Should().Be(2);
                (await db.TrustedDevices.CountAsync()).Should().Be(1);
                // SetNull 参照も維持されていること。
                (await db.PermissionBundles.CountAsync(x => x.CreatedBy != null)).Should().Be(1);

                // --- 4. 追加列が既存行に安全な既定値で入っていること ---
                users.Should().OnlyContain(u => !u.IsPasswordSetupPending);
                users.Should().OnlyContain(u => u.PasswordSetupExpiresAt == null);
                users.Should().OnlyContain(u => u.WindowsAccountName == null);
                users.Should().OnlyContain(u => !u.IsDisabled);
                users.Should().OnlyContain(u => u.DisabledAt == null);
                users.Should().OnlyContain(u => u.DisabledReason == null);
                users.Should().OnlyContain(u => u.DisabledByUserId == null);
                users.Should().OnlyContain(u => u.DisabledByUsername == null);
            }

            // --- 5. テーブル再構築の痕跡が残っていないこと ---
            await using (var db = CreateContext(dbPath))
            {
                var leftovers = await db.Database
                    .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE name LIKE 'ef_temp_%'")
                    .ToListAsync();
                leftovers.Should().BeEmpty();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// 旧スキーマに対して生 SQL で投入する。EF のエンティティは新しい列を持っているため
    /// この時点では使えない (旧スキーマにその列がまだ無い)。
    /// </summary>
    private static async Task SeedLegacyDataAsync(AppDbContext db)
    {
        const string ts = "2026-01-01T00:00:00.0000000Z";

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO Users (Id, Username, PasswordHash, IsAdmin, IsLocked, FailedLoginCount,
                               PasswordChangedAt, PasswordExpiresAt, MustChangePassword, CreatedAt)
            VALUES (1, 'alice', 'HASH-ALICE', 1, 0, 0, '{ts}', '{ts}', 0, '{ts}'),
                   (2, 'bob',   'HASH-BOB',   0, 0, 0, '{ts}', '{ts}', 0, '{ts}');

            INSERT INTO TrustedDevices (Id, UserId, MachineName, WindowsUsername, DeviceTokenHash,
                                        RegisteredAt, LastUsedAt, IsRevoked)
            VALUES (1, 1, 'PC-01', 'alice', 'DEVICE-HASH', '{ts}', '{ts}', 0);

            INSERT INTO RefreshTokens (Id, UserId, TokenHash, DeviceId, IssuedAt, ExpiresAt, LastUsedAt, IsRevoked)
            VALUES ('rt-1', 1, 'TOKEN-HASH-1', 1,    '{ts}', '{ts}', '{ts}', 0),
                   ('rt-2', 2, 'TOKEN-HASH-2', NULL, '{ts}', '{ts}', '{ts}', 0);

            INSERT INTO ExecutionNodes (Id, Name, NodeType, IsActive, HealthStatus, MaxConcurrency, CreatedAt)
            VALUES (1, 'Direct (Local)', 'Direct', 1, 'Healthy', 20, '{ts}');

            INSERT INTO CifsHosts (Id, Name, HostAddress, Port, CredUsername, CredPasswordEnc, ExecutionNodeId, CreatedAt)
            VALUES (1, 'FS-01', '192.0.2.10', 445, 'svc', X'00', 1, '{ts}');

            INSERT INTO CifsShares (Id, HostId, ShareName, DisplayName)
            VALUES (1, 1, 'share', '共有');

            INSERT INTO PermissionTemplates (Id, Name, CanRead, CanWrite, CanDelete, CanRename)
            VALUES (1, 'フルアクセス', 1, 1, 1, 1);

            INSERT INTO UserPermissions (Id, UserId, ShareId, TemplateId, AllowedPath, CreatedAt, CreatedBy)
            VALUES (1, 1, 1, 1, '/dept-A', '{ts}', 1),
                   (2, 2, 1, 1, '/dept-B', '{ts}', 1);

            INSERT INTO PermissionBundles (Id, Name, CreatedAt, CreatedBy)
            VALUES (1, '経理部セット', '{ts}', 1);
            """);
    }
}
