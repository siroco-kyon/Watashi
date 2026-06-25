using FluentAssertions;
using Microsoft.Data.Sqlite;
using Watashi.Server.Services;

namespace Watashi.Tests;

public class DatabaseBackupCommandTests
{
    [Fact]
    public void Run_creates_consistent_sqlite_backup()
    {
        var root = Path.Combine(Path.GetTempPath(), "watashi-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.db");
        var outputPath = Path.Combine(root, "backup", "copy.db");
        try
        {
            using (var source = new SqliteConnection($"Data Source={sourcePath}"))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "CREATE TABLE Sample (Value TEXT NOT NULL); INSERT INTO Sample VALUES ('ok');";
                command.ExecuteNonQuery();
            }

            var exitCode = DatabaseBackupCommand.Run(new[]
            {
                "--backup", "--database", sourcePath, "--output", outputPath,
            });

            exitCode.Should().Be(0);
            File.Exists(outputPath).Should().BeTrue();
            using var backup = new SqliteConnection($"Data Source={outputPath};Mode=ReadOnly");
            backup.Open();
            using var query = backup.CreateCommand();
            query.CommandText = "SELECT Value FROM Sample";
            query.ExecuteScalar().Should().Be("ok");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Run_returns_failure_and_removes_output_for_missing_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "watashi-backup-test-" + Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(root, "copy.db");
        try
        {
            var exitCode = DatabaseBackupCommand.Run(new[]
            {
                "--backup", "--database", Path.Combine(root, "missing.db"), "--output", outputPath,
            });

            exitCode.Should().Be(1);
            File.Exists(outputPath).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Run_rejects_same_source_and_output_without_deleting_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "watashi-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.db");
        try
        {
            using (var source = new SqliteConnection($"Data Source={sourcePath}"))
            {
                source.Open();
                using var command = source.CreateCommand();
                command.CommandText = "CREATE TABLE Sample (Value TEXT NOT NULL); INSERT INTO Sample VALUES ('safe');";
                command.ExecuteNonQuery();
            }

            var aliasOfSourcePath = Path.Combine(root, ".", "source.db");
            var exitCode = DatabaseBackupCommand.Run(new[]
            {
                "--backup", "--database", sourcePath, "--output", aliasOfSourcePath,
            });

            exitCode.Should().Be(1);
            File.Exists(sourcePath).Should().BeTrue();
            using var remaining = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
            remaining.Open();
            using var query = remaining.CreateCommand();
            query.CommandText = "SELECT Value FROM Sample";
            query.ExecuteScalar().Should().Be("safe");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
