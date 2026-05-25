using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

public class AuditLogPurgeTests
{
    [Fact]
    public async Task Logs_older_than_retention_should_be_purgeable()
    {
        using var db = new TestDb();
        var now = DateTime.UtcNow;
        db.Db.AuditLogs.AddRange(
            new AuditLog { Timestamp = now.AddDays(-400), Username = "old1", Operation = Operations.Read, Result = AuditResults.Success },
            new AuditLog { Timestamp = now.AddDays(-366), Username = "old2", Operation = Operations.Read, Result = AuditResults.Success },
            new AuditLog { Timestamp = now.AddDays(-100), Username = "fresh", Operation = Operations.Read, Result = AuditResults.Success });
        await db.Db.SaveChangesAsync();

        // 365 日保管に相当する閾値: 365 日以前のものは削除対象。
        var threshold = now.AddDays(-365);
        var stale = db.Db.AuditLogs.Where(l => l.Timestamp < threshold).ToList();
        stale.Should().HaveCount(2);
        stale.Select(s => s.Username).Should().BeEquivalentTo(new[] { "old1", "old2" });
    }

    [Fact]
    public async Task AuditLog_constraint_only_allows_success_or_failure_result()
    {
        using var db = new TestDb();
        db.Db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            Username = "u",
            Operation = Operations.Read,
            Result = "bogus",   // チェック制約に違反する
        });
        Func<Task> act = () => db.Db.SaveChangesAsync();
        await act.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>();
    }

    [Fact]
    public async Task AuditLog_accepts_success_and_failure_results()
    {
        using var db = new TestDb();
        var now = DateTime.UtcNow;
        db.Db.AuditLogs.AddRange(
            new AuditLog { Timestamp = now, Username = "u", Operation = Operations.Read, Result = AuditResults.Success },
            new AuditLog { Timestamp = now, Username = "u", Operation = Operations.Write, Result = AuditResults.Failure });
        await db.Db.SaveChangesAsync();
        db.Db.AuditLogs.Should().HaveCount(2);
    }
}
