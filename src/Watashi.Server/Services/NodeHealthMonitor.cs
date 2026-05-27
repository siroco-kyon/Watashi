using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Constants;

namespace Watashi.Server.Services;

/// <summary>
/// 90 秒以上ハートビートが無い Agent ノードを Unhealthy にし、復活したら Healthy に戻す。
/// Gateway 配下で一度も heartbeat していない Agent は Unknown のままにして、操作時の HTTP 到達性で判定する。
/// 状態変化があったノードだけを ExecuteUpdate で書き込み、no-op の SaveChanges を避ける。
/// </summary>
public class NodeHealthMonitor : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<NodeHealthMonitor> _log;
    private readonly TimeSpan _unhealthyAfter = TimeSpan.FromSeconds(90);

    public NodeHealthMonitor(IServiceProvider sp, ILogger<NodeHealthMonitor> log)
    {
        _sp = sp; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = _sp.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var threshold = DateTime.UtcNow - _unhealthyAfter;
                var snapshot = await db.ExecutionNodes.AsNoTracking()
                    .Where(n => n.NodeType == NodeTypes.Agent && (n.GatewayNodeId == null || n.LastHeartbeatAt != null))
                    .Select(n => new { n.Id, n.HealthStatus, n.LastHeartbeatAt })
                    .ToListAsync(ct);
                foreach (var n in snapshot)
                {
                    var desired = n.LastHeartbeatAt.HasValue && n.LastHeartbeatAt >= threshold
                        ? HealthStatuses.Healthy : HealthStatuses.Unhealthy;
                    if (n.HealthStatus == desired) continue;
                    await db.ExecutionNodes.Where(x => x.Id == n.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.HealthStatus, desired), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "NodeHealthMonitor ループ失敗"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
