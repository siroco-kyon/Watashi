using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Constants;

namespace Watashi.Server.Services;

/// <summary>
/// AuditLogs の保管期間超過分を日次でパージする。
/// 保管期間は SystemSettings.AuditLogRetentionDays (デフォルト 365 日) で設定可能。
/// 0 以下を指定するとパージ停止 (永久保管)。
/// </summary>
public class AuditLogPurgeService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<AuditLogPurgeService> _log;
    // 起動 30 秒後に初回実行、以降 24 時間毎。
    private readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    public AuditLogPurgeService(IServiceProvider sp, ILogger<AuditLogPurgeService> log)
    {
        _sp = sp; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(_initialDelay, ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        do
        {
            try { await PurgeOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "AuditLogPurge ループ失敗"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PurgeOnceAsync(CancellationToken ct)
    {
        await using var scope = _sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var setting = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.AuditLogRetentionDays, ct);
        if (setting is null || !int.TryParse(setting.Value, out var days) || days <= 0 || days > 365_000)
        {
            _log.LogDebug("AuditLogPurge: 保管日数が未設定 or 0 以下のためスキップ");
            return;
        }
        var threshold = DateTime.UtcNow.AddDays(-days);
        var deleted = await db.AuditLogs
            .Where(l => l.Timestamp < threshold)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            _log.LogInformation("AuditLogPurge: {Count} 件削除 (保管 {Days} 日, 閾値 {Threshold:O})", deleted, days, threshold);
    }
}
