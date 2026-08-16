namespace Watashi.Server.Services;

/// <summary>期限切れ Transfer v2 session と同一親にある一時ファイルを定期回収する。</summary>
public sealed class UploadSessionJanitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UploadSessionJanitor> _logger;
    private readonly TimeSpan _interval;

    public UploadSessionJanitor(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<UploadSessionJanitor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue<int?>("TransferV2:JanitorIntervalMinutes") ?? 15, 1, 24 * 60));
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
            var service = scope.ServiceProvider.GetRequiredService<UploadSessionService>();
            var count = await service.ExpireSessionsAsync(ct);
            if (count > 0)
                _logger.LogInformation("Transfer v2 の期限切れ session を {Count} 件回収しました", count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transfer v2 session janitor の実行に失敗しました");
        }
    }
}
