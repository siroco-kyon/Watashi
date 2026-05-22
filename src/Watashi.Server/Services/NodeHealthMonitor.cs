using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Constants;

namespace Watashi.Server.Services;

/// <summary>
/// 30 秒以上ハートビートが無い Agent ノードを Unhealthy にし、復活したら Healthy へ戻す。
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var threshold = DateTime.UtcNow - _unhealthyAfter;
                var nodes = await db.ExecutionNodes
                    .Where(n => n.NodeType == NodeTypes.Agent)
                    .ToListAsync(ct);
                bool dirty = false;
                foreach (var n in nodes)
                {
                    var desired = n.LastHeartbeatAt.HasValue && n.LastHeartbeatAt >= threshold
                        ? HealthStatuses.Healthy
                        : HealthStatuses.Unhealthy;
                    if (n.HealthStatus != desired)
                    {
                        n.HealthStatus = desired;
                        dirty = true;
                    }
                }
                if (dirty) await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "NodeHealthMonitor ループ失敗"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
