using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public sealed class AuditOutboxTests
{
    [Fact]
    public async Task Dispatcher_deduplicates_event_already_committed_before_outbox_cleanup()
    {
        using var testDb = new TestDb();
        var eventId = Guid.NewGuid();
        var log = CreateLog(eventId);
        testDb.Db.AuditLogs.Add(log);
        testDb.Db.AuditOutboxEntries.Add(new AuditOutboxEntry
        {
            EventId = eventId,
            PayloadJson = JsonSerializer.Serialize(log),
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            NextAttemptAt = DateTime.UtcNow.AddSeconds(-1),
        });
        await testDb.Db.SaveChangesAsync();
        testDb.Db.ChangeTracker.Clear();

        var count = await AuditOutboxDispatcher.DrainOnceAsync(testDb.Db, DateTime.UtcNow);

        count.Should().Be(1);
        (await testDb.Db.AuditLogs.CountAsync(x => x.EventId == eventId)).Should().Be(1);
        (await testDb.Db.AuditOutboxEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Dispatcher_keeps_failed_event_and_schedules_retry()
    {
        using var testDb = new TestDb();
        await testDb.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_all_audit
            BEFORE INSERT ON AuditLogs
            BEGIN
                SELECT RAISE(ABORT, 'audit unavailable');
            END;
            """);
        var eventId = Guid.NewGuid();
        var log = CreateLog(eventId);
        var due = DateTime.UtcNow.AddSeconds(-1);
        testDb.Db.AuditOutboxEntries.Add(new AuditOutboxEntry
        {
            EventId = eventId,
            PayloadJson = JsonSerializer.Serialize(log),
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            NextAttemptAt = due,
        });
        await testDb.Db.SaveChangesAsync();
        testDb.Db.ChangeTracker.Clear();

        var count = await AuditOutboxDispatcher.DrainOnceAsync(testDb.Db, DateTime.UtcNow);

        count.Should().Be(0);
        var retained = await testDb.Db.AuditOutboxEntries.AsNoTracking().SingleAsync();
        retained.AttemptCount.Should().Be(1);
        retained.NextAttemptAt.Should().BeAfter(due);
        retained.LastError.Should().Contain("audit unavailable");
    }

    private static AuditLog CreateLog(Guid id) => new()
    {
        EventId = id,
        Timestamp = DateTime.UtcNow,
        Username = "alice",
        Operation = Operations.Copy,
        Result = AuditResults.Success,
    };
}
