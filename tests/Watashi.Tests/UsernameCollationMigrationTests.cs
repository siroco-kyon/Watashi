using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watashi.Server.Data;

namespace Watashi.Tests;

public class UsernameCollationMigrationTests
{
    private const string PreviousMigration = "AddPasswordSetupPending";

    [Fact]
    public async Task Migration_stops_when_case_only_duplicates_already_exist()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"watashi-username-collision-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = CreateContext(dbPath))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                const string ts = "2026-01-01T00:00:00.0000000Z";
                await db.Database.ExecuteSqlRawAsync($"""
                    INSERT INTO Users
                        (Username, PasswordHash, IsAdmin, IsLocked, FailedLoginCount,
                         PasswordChangedAt, PasswordExpiresAt, MustChangePassword, CreatedAt)
                    VALUES
                        ('KU_EM', 'HASH-1', 0, 0, 0, '{ts}', '{ts}', 0, '{ts}'),
                        ('ku_em', 'HASH-2', 0, 0, 0, '{ts}', '{ts}', 0, '{ts}');
                    """);
            }

            await using (var db = CreateContext(dbPath))
            {
                var migrate = async () => await db.Database.MigrateAsync();

                await migrate.Should().ThrowAsync<SqliteException>()
                    .WithMessage("*UNIQUE constraint failed*");
            }

            await using (var db = CreateContext(dbPath))
            {
                (await db.Database
                    .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Users")
                    .SingleAsync()).Should().Be(2);
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
}
