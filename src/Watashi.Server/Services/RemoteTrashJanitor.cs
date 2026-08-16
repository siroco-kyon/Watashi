namespace Watashi.Server.Services;

/// <summary>保管期限を超えたリモートごみ箱項目を定期的に完全削除する。</summary>
public sealed class RemoteTrashJanitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RemoteTrashJanitor> _logger;
    private readonly TimeSpan _interval;

    public RemoteTrashJanitor(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<RemoteTrashJanitor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue<int?>("Trash:JanitorIntervalMinutes") ?? 60, 1, 24 * 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken);
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<RemoteTrashService>();
            var count = await service.PurgeExpiredAsync(ct);
            if (count > 0)
                _logger.LogInformation("期限切れのリモートごみ箱項目を {Count} 件完全削除しました", count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "リモートごみ箱janitorの実行に失敗しました");
        }
    }
}
