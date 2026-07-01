using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Watashi.Agent.Data;

namespace Watashi.Agent.Services;

public class LogSyncService : BackgroundService
{
    private const int MaxAttempts = 50;
    private readonly ILogger<LogSyncService> _log;
    private readonly IServiceProvider _sp;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private int _consecutiveFailures;

    public LogSyncService(ILogger<LogSyncService> log, IServiceProvider sp, IHttpClientFactory http, IConfiguration cfg)
    {
        _log = log; _sp = sp; _http = http; _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var central = _cfg["Agent:CentralUrl"];
        if (string.IsNullOrWhiteSpace(central))
        {
            _log.LogWarning("Agent:CentralUrl が未設定のため LogSync をスキップします。");
            return;
        }
        var client = _http.CreateClient("central");
        client.BaseAddress = new Uri(central);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            var delay = ComputeBackoff();
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }
            try
            {
                await SendBatchAsync(client, ct);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _log.LogWarning(ex, "LogSync 失敗 (連続 {N})", _consecutiveFailures);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private TimeSpan ComputeBackoff()
    {
        if (_consecutiveFailures == 0) return TimeSpan.Zero;
        var seconds = Math.Min(300, Math.Pow(2, _consecutiveFailures));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>送信を諦めた (MaxAttempts 到達) ログの保持期間。経過後に削除して SQLite の肥大を防ぐ。</summary>
    private static readonly TimeSpan DeadLogRetention = TimeSpan.FromDays(7);

    private async Task SendBatchAsync(HttpClient client, CancellationToken ct)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        // MaxAttempts 到達分は送信対象外のまま残り続けるため、保持期間経過後にパージする。
        var deadCutoff = DateTime.UtcNow - DeadLogRetention;
        var purged = await db.PendingLogs
            .Where(p => p.AttemptCount >= MaxAttempts && p.CreatedAt < deadCutoff)
            .ExecuteDeleteAsync(ct);
        if (purged > 0)
            _log.LogWarning("LogSync: 送信を諦めた監査ログ {Count} 件を破棄しました (保持 {Days} 日超過)", purged, DeadLogRetention.TotalDays);

        var pending = await db.PendingLogs.AsNoTracking()
            .Where(p => p.AttemptCount < MaxAttempts)
            .OrderBy(p => p.Id).Take(200).ToListAsync(ct);
        if (pending.Count == 0) return;

        var payload = new { items = pending.Select(p => p.LogJson).ToArray() };
        using var res = await client.PostAsJsonAsync("/api/internal/audit-logs/batch", payload, ct);
        if (res.IsSuccessStatusCode)
        {
            var ids = pending.Select(p => p.Id).ToList();
            await db.PendingLogs.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct);
        }
        else
        {
            var ids = pending.Select(p => p.Id).ToList();
            await db.PendingLogs.Where(p => ids.Contains(p.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.AttemptCount, p => p.AttemptCount + 1), ct);
            throw new IOException($"central HTTP {(int)res.StatusCode}");
        }
    }
}
