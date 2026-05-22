using System.Net.Http.Json;

namespace Watashi.Agent.Services;

public class HeartbeatService : BackgroundService
{
    private readonly ILogger<HeartbeatService> _log;
    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _http;

    public HeartbeatService(ILogger<HeartbeatService> log, IConfiguration cfg, IHttpClientFactory http)
    {
        _log = log;
        _cfg = cfg;
        _http = http;
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
                    _log.LogWarning("Heartbeat 非 2xx: {Code}", (int)res.StatusCode);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Heartbeat 送信失敗"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
