using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;

namespace Watashi.Server.Services;

/// <summary>期限切れ認証レコードを安全な猶予期間後に整理し、長期運用時のDB肥大を抑える。</summary>
public sealed class DatabaseMaintenanceService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseMaintenanceService> _logger;

    public DatabaseMaintenanceService(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<DatabaseMaintenanceService> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 起動直後のmigration/seedや利用者ログインと競合しないよう最初は少し待つ。
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var retentionDays = Math.Max(7,
                    _configuration.GetValue<int?>("Operations:ExpiredTokenRetentionDays") ?? 30);
                var deleted = await PurgeExpiredAuthenticationAsync(
                    db, DateTime.UtcNow, TimeSpan.FromDays(retentionDays), stoppingToken);
                if (deleted > 0)
                    _logger.LogInformation("期限切れ/失効済みrefresh tokenを {Count} 件整理しました。", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DBメンテナンスに失敗しました。次回周期で再試行します。");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal static Task<int> PurgeExpiredAuthenticationAsync(
        AppDbContext db,
        DateTime now,
        TimeSpan retention,
        CancellationToken ct = default)
    {
        var cutoff = now - retention;
        // 失効済みtokenも一定期間残すことで、再利用検知と調査証跡を維持する。
        return db.RefreshTokens
            .Where(token => token.ExpiresAt < cutoff ||
                            (token.IsRevoked && token.LastUsedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }
}
