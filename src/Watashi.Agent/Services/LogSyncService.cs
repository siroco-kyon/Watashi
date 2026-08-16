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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException ex)
            {
                _consecutiveFailures++;
                _log.LogWarning(ex, "LogSync request timed out (consecutive failures: {N})", _consecutiveFailures);
            }
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

    private async Task SendBatchAsync(HttpClient client, CancellationToken ct)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var pending = await db.PendingLogs.AsNoTracking()
            .Where(p => p.AttemptCount < MaxAttempts)
            .OrderBy(p => p.Id).Take(200).ToListAsync(ct);
        if (pending.Count == 0) return;

        var payload = new { items = pending.Select(p => p.LogJson).ToArray() };
        using var res = await client.PostAsJsonAsync("/api/internal/audit-logs/batch", payload, ct);
        if (res.IsSuccessStatusCode)
        {
            var ack = await res.Content.ReadFromJsonAsync<AuditBatchAck>(cancellationToken: ct);
            if (ack is not null)
            {
                await ApplyAcknowledgementAsync(db, pending, ack, ct);
                if (ack.Rejected.Length > 0)
                    _log.LogError("LogSync: centralが監査ログ {Count} 件を拒否しました。端末DBに保持します。",
                        ack.Rejected.Length);
            }
            else
            {
                throw new InvalidDataException("centralの監査ログACKが空です。");
            }
        }
        else
        {
            // HTTP/ネットワーク障害はレコード自体の不正ではないためAttemptCountへ加算しない。
            // 復旧まで破棄せず、サービス全体の指数バックオフで再送する。
            throw new IOException($"central HTTP {(int)res.StatusCode}");
        }
    }

    internal static async Task ApplyAcknowledgementAsync(
        AgentDbContext db,
        IReadOnlyList<PendingLog> pending,
        AuditBatchAck ack,
        CancellationToken ct = default)
    {
        var acceptedIds = ack.Accepted
            .Where(index => index >= 0 && index < pending.Count)
            .Select(index => pending[index].Id)
            .Distinct()
            .ToArray();
        var rejectedIds = ack.Rejected
            .Where(item => item.Index >= 0 && item.Index < pending.Count)
            .Select(item => pending[item.Index].Id)
            .Distinct()
            .Except(acceptedIds)
            .ToArray();
        if (acceptedIds.Length > 0)
            await db.PendingLogs.Where(p => acceptedIds.Contains(p.Id)).ExecuteDeleteAsync(ct);
        if (rejectedIds.Length > 0)
            await db.PendingLogs.Where(p => rejectedIds.Contains(p.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(
                    p => p.AttemptCount,
                    p => p.AttemptCount + 1), ct);
    }

    internal sealed record AuditRejectedItem(int Index, string Error);
    internal sealed record AuditBatchAck(int[] Accepted, AuditRejectedItem[] Rejected);
}
