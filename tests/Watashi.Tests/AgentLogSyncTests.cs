using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Watashi.Agent.Data;
using Watashi.Agent.Services;

namespace Watashi.Tests;

public sealed class AgentLogSyncTests
{
    [Fact]
    public async Task Partial_ack_deletes_only_accepted_and_retains_rejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AgentDbContext>().UseSqlite(connection).Options;
        await using var db = new AgentDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var rows = Enumerable.Range(0, 3).Select(index => new PendingLog
        {
            LogJson = $"{{\"index\":{index}}}",
            CreatedAt = DateTime.UtcNow,
        }).ToArray();
        db.PendingLogs.AddRange(rows);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var pending = await db.PendingLogs.AsNoTracking().OrderBy(x => x.Id).ToListAsync();

        await LogSyncService.ApplyAcknowledgementAsync(db, pending,
            new LogSyncService.AuditBatchAck(
                [0, 2],
                [new LogSyncService.AuditRejectedItem(1, "invalid_payload")]),
            CancellationToken.None);

        var retained = await db.PendingLogs.AsNoTracking().SingleAsync();
        retained.Id.Should().Be(rows[1].Id);
        retained.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Invalid_ack_indices_never_delete_unrelated_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AgentDbContext>().UseSqlite(connection).Options;
        await using var db = new AgentDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.PendingLogs.Add(new PendingLog { LogJson = "{}", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var pending = await db.PendingLogs.AsNoTracking().ToListAsync();

        await LogSyncService.ApplyAcknowledgementAsync(db, pending,
            new LogSyncService.AuditBatchAck(
                [-1, 99],
                [new LogSyncService.AuditRejectedItem(42, "bad")]),
            CancellationToken.None);

        (await db.PendingLogs.CountAsync()).Should().Be(1);
    }
}
