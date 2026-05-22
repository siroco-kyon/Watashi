using Microsoft.EntityFrameworkCore;
using Watashi.Agent.Data;

namespace Watashi.Agent.Services;

public class LogSyncService : BackgroundService
{
    private readonly ILogger<LogSyncService> _log;
    private readonly IServiceProvider _sp;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;

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
        while (!ct.IsCancellationRequested)
        {
            try { await SendBatchAsync(client, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "LogSync 失敗"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendBatchAsync(HttpClient client, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var pending = await db.PendingLogs.OrderBy(p => p.Id).Take(200).ToListAsync(ct);
        if (pending.Count == 0) return;

        var payload = new { items = pending.Select(p => p.LogJson).ToArray() };
        using var res = await client.PostAsJsonAsync("/api/internal/audit-logs/batch", payload, ct);
        if (res.IsSuccessStatusCode)
        {
            db.PendingLogs.RemoveRange(pending);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            foreach (var p in pending) p.AttemptCount++;
            await db.SaveChangesAsync(ct);
        }
    }
}
