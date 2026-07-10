using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Watashi.Agent.Services;

public class HeartbeatService : BackgroundService
{
    private readonly ILogger<HeartbeatService> _log;
    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _http;
    private readonly ConcurrencyLimiter _limiter;

    public HeartbeatService(ILogger<HeartbeatService> log, IConfiguration cfg, IHttpClientFactory http, ConcurrencyLimiter limiter)
    {
        _log = log;
        _cfg = cfg;
        _http = http;
        _limiter = limiter;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var central = _cfg["Agent:CentralUrl"];
        var agentId = _cfg["Agent:AgentId"] ?? "agent";
        if (string.IsNullOrWhiteSpace(central))
        {
            _log.LogWarning("Agent:CentralUrl が未設定のため Heartbeat をスキップします。");
            return;
        }

        var client = _http.CreateClient("central");
        client.BaseAddress = new Uri(central);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var res = await client.PostAsJsonAsync("/api/internal/heartbeat",
                    new { agentId, timestamp = DateTime.UtcNow }, ct);
                if (!res.IsSuccessStatusCode)
                {
                    _log.LogWarning("Heartbeat 非 2xx: {Code}", (int)res.StatusCode);
                    continue;
                }
                // サーバが管理画面で更新した MaxConcurrency を返してくる。受け取り次第ローカルへ反映する。
                // 旧実装は appsettings.json の Agent:MaxConcurrency しか参照しないため、管理画面で
                // 変更しても Agent 再起動まで反映されなかった。
                try
                {
                    var ack = await res.Content.ReadFromJsonAsync<HeartbeatAck>(cancellationToken: ct);
                    if (ack is not null && ack.MaxConcurrency > 0 && ack.MaxConcurrency != _limiter.Max)
                    {
                        _log.LogInformation("MaxConcurrency を {Old} -> {New} に更新", _limiter.Max, ack.MaxConcurrency);
                        _limiter.SetMax(ack.MaxConcurrency);
                    }
                }
                catch (Exception parseEx) when (!ct.IsCancellationRequested)
                {
                    // 旧サーバ (NoContent) 互換: 読めなくても heartbeat 自体は成功扱い。
                    _log.LogDebug(parseEx, "Heartbeat 応答パース失敗 (旧サーバの可能性)");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException ex) { _log.LogWarning(ex, "Heartbeat request timed out"); }
            catch (Exception ex) { _log.LogWarning(ex, "Heartbeat 送信失敗"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private class HeartbeatAck
    {
        [JsonPropertyName("maxConcurrency")] public int MaxConcurrency { get; set; }
    }
}
