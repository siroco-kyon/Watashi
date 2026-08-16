using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watashi.Server.Data;

namespace Watashi.Tests;

public sealed class TransferV2MigrationTests
{
    [Fact]
    public async Task Migration_from_permission_validity_adds_only_persistent_upload_schema()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"watashi-transfer-v2-{Guid.NewGuid():N}.db");
        try
        {
            await using (var before = CreateContext(dbPath))
            {
                await before.GetService<IMigrator>()
                    .MigrateAsync("20260814114232_AddUserPermissionValidity");
                (await TableNamesAsync(before)).Should().NotContain("UploadSessions");
            }

            await using (var after = CreateContext(dbPath))
            {
                await after.Database.MigrateAsync();
                (await TableNamesAsync(after)).Should().Contain("UploadSessions");

                var columns = await after.Database.SqlQueryRaw<string>(
                    "SELECT name AS Value FROM pragma_table_info('UploadSessions')")
                    .ToListAsync();
                columns.Should().Contain(new[]
                {
                    "Id", "UserId", "HostId", "ShareId", "TargetPath", "TempPath",
                    "IdempotencyKeyHash", "TotalSize", "ExpectedSha256", "UploadedOffset",
                    "Status", "ExpiresAt",
                });

                var indexes = await after.Database.SqlQueryRaw<string>(
                    "SELECT name AS Value FROM sqlite_master WHERE type='index' AND tbl_name='UploadSessions'")
                    .ToListAsync();
                indexes.Should().Contain("IX_UploadSessions_UserId_IdempotencyKeyHash");
                indexes.Should().Contain("IX_UploadSessions_Status_ExpiresAt");

                var createSql = await after.Database.SqlQueryRaw<string>(
                    "SELECT sql AS Value FROM sqlite_master WHERE type='table' AND name='UploadSessions'")
                    .SingleAsync();
                createSql.Should().Contain("CK_UploadSession_Status")
                    .And.Contain("CK_UploadSession_Offset");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static AppDbContext CreateContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True;")
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return new AppDbContext(options);
    }

    private static Task<List<string>> TableNamesAsync(AppDbContext db)
        => db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type='table'")
            .ToListAsync();
}
