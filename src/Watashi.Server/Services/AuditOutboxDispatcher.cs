using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

/// <summary>監査outboxを少量ずつ再送する。失敗イベントは破棄せず次回へ持ち越す。</summary>
public sealed class AuditOutboxDispatcher : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AuditOutboxDispatcher> _logger;

    public AuditOutboxDispatcher(IServiceProvider services, ILogger<AuditOutboxDispatcher> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var count = await DrainOnceAsync(
                    scope.ServiceProvider.GetRequiredService<AppDbContext>(), DateTime.UtcNow, stoppingToken);
                if (count > 0)
                    _logger.LogInformation("監査outboxから {Count} 件を保存しました。", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "監査outboxの再送に失敗しました。次回周期で再試行します。");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal static async Task<int> DrainOnceAsync(
        AppDbContext db, DateTime now, CancellationToken ct = default)
    {
        var pending = await db.AuditOutboxEntries
            .Where(e => e.NextAttemptAt <= now)
            .OrderBy(e => e.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        var completed = 0;
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            AuditLog? log;
            try { log = JsonSerializer.Deserialize<AuditLog>(entry.PayloadJson); }
            catch (JsonException ex)
            {
                entry.AttemptCount++;
                entry.LastError = Limit(ex.Message);
                entry.NextAttemptAt = NextAttempt(now, entry.AttemptCount);
                await db.SaveChangesAsync(ct);
                continue;
            }
            if (log is null || log.EventId != entry.EventId)
            {
                entry.AttemptCount++;
                entry.LastError = "監査outbox payloadのeventIdが一致しません。";
                entry.NextAttemptAt = NextAttempt(now, entry.AttemptCount);
                await db.SaveChangesAsync(ct);
                continue;
            }

            try
            {
                if (!await db.AuditLogs.AsNoTracking().AnyAsync(x => x.EventId == entry.EventId, ct))
                    db.AuditLogs.Add(log);
                db.AuditOutboxEntries.Remove(entry);
                await db.SaveChangesAsync(ct);
                completed++;
            }
            catch (Exception ex)
            {
                try { db.Entry(log).State = EntityState.Detached; } catch { }
                db.Entry(entry).State = EntityState.Modified;
                entry.AttemptCount++;
                entry.LastError = Limit(ex.GetBaseException().Message);
                entry.NextAttemptAt = NextAttempt(now, entry.AttemptCount);
                try { await db.SaveChangesAsync(ct); } catch { db.ChangeTracker.Clear(); }
            }
        }
        return completed;
    }

    private static DateTime NextAttempt(DateTime now, int attempts)
        => now.AddSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Min(8, attempts))));

    private static string Limit(string value) => value.Length <= 1000 ? value : value[..1000];
}
