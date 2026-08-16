using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Server.Services;

/// <summary>
/// 単純なプロセス生存確認とは別に、DB・ディスク・バックアップ・証明書・実行ノードを診断する。
/// 診断は参照専用で、SMBへ新しい接続を張らないため管理画面の更新が利用者の転送を妨げない。
/// </summary>
public sealed class OperationalDiagnosticsService
{
    private const long DefaultDiskWarningBytes = 5L * 1024 * 1024 * 1024;
    private const long DefaultDiskCriticalBytes = 512L * 1024 * 1024;
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public OperationalDiagnosticsService(AppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task<OperationalStatusDto> GetAsync(CancellationToken ct = default)
    {
        var result = new OperationalStatusDto { CheckedAt = DateTime.UtcNow };
        var databaseAvailable = await CheckDatabaseAsync(result.Database, ct);
        result.AuditOutbox = databaseAvailable
            ? await CheckAuditOutboxAsync(ct)
            : new DiagnosticItemDto
            {
                Status = DiagnosticStatuses.Unhealthy,
                Message = "DBへ接続できないため監査再送待ち件数を確認できません。",
            };
        result.Disk = CheckDisk();
        result.Backup = CheckBackup();
        result.Certificate = CheckCertificate();
        result.Nodes = databaseAvailable ? await CheckNodesAsync(ct) : new List<NodeDiagnosticDto>();
        result.Status = CalculateOverallStatus(result);
        return result;
    }

    private async Task<DiagnosticItemDto> CheckAuditOutboxAsync(CancellationToken ct)
    {
        var count = await _db.AuditOutboxEntries.AsNoTracking().LongCountAsync(ct);
        var oldest = count == 0
            ? null
            : await _db.AuditOutboxEntries.AsNoTracking()
                .OrderBy(x => x.CreatedAt)
                .Select(x => (DateTime?)x.CreatedAt)
                .FirstOrDefaultAsync(ct);
        return new DiagnosticItemDto
        {
            Status = count == 0
                ? DiagnosticStatuses.Healthy
                : count >= 1000 ? DiagnosticStatuses.Unhealthy : DiagnosticStatuses.Degraded,
            Value = count,
            ObservedAt = oldest,
            Message = count == 0
                ? "監査ログの再送待ちはありません。"
                : $"監査ログ {count:N0} 件が再送待ちです。最古: {oldest:yyyy-MM-dd HH:mm:ss} UTC",
        };
    }

    private async Task<bool> CheckDatabaseAsync(DiagnosticItemDto item, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!await _db.Database.CanConnectAsync(ct))
                throw new InvalidOperationException("SQLiteへ接続できません。");
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            stopwatch.Stop();
            item.Status = DiagnosticStatuses.Healthy;
            item.Value = stopwatch.ElapsedMilliseconds;
            item.Message = $"接続・クエリ成功 ({stopwatch.ElapsedMilliseconds} ms)";
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            item.Status = DiagnosticStatuses.Unhealthy;
            item.Value = stopwatch.ElapsedMilliseconds;
            item.Message = $"DB診断失敗: {SafeMessage(ex)}";
            return false;
        }
    }

    private DiagnosticItemDto CheckDisk()
    {
        try
        {
            var connection = new SqliteConnectionStringBuilder(
                _configuration.GetConnectionString("Default") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(connection.DataSource))
                throw new InvalidOperationException("DBファイルの場所が設定されていません。");

            var fullPath = Path.GetFullPath(connection.DataSource);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("DBドライブを判定できません。");
            var drive = new DriveInfo(root);
            var free = drive.AvailableFreeSpace;
            var warning = _configuration.GetValue<long?>("Operations:DiskWarningBytes") ?? DefaultDiskWarningBytes;
            var critical = _configuration.GetValue<long?>("Operations:DiskCriticalBytes") ?? DefaultDiskCriticalBytes;
            var status = free < critical
                ? DiagnosticStatuses.Unhealthy
                : free < warning ? DiagnosticStatuses.Degraded : DiagnosticStatuses.Healthy;
            return new DiagnosticItemDto
            {
                Status = status,
                Value = free,
                Message = $"空き容量 {FormatBytes(free)}",
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticItemDto
            {
                Status = DiagnosticStatuses.Degraded,
                Message = $"ディスク診断失敗: {SafeMessage(ex)}",
            };
        }
    }

    private DiagnosticItemDto CheckBackup()
    {
        var directory = _configuration["Operations:BackupDirectory"];
        if (string.IsNullOrWhiteSpace(directory))
            return new DiagnosticItemDto
            {
                Status = DiagnosticStatuses.NotConfigured,
                Message = "バックアップ監視先が未設定です。",
            };

        try
        {
            if (!Directory.Exists(directory))
                return new DiagnosticItemDto
                {
                    Status = DiagnosticStatuses.Degraded,
                    Message = "バックアップディレクトリが見つかりません。",
                };

            var latest = new DirectoryInfo(directory).EnumerateFiles("*.db", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null)
                return new DiagnosticItemDto
                {
                    Status = DiagnosticStatuses.Degraded,
                    Message = "バックアップファイルがありません。",
                };

            var maxAgeHours = Math.Max(1, _configuration.GetValue<int?>("Operations:BackupMaxAgeHours") ?? 26);
            var age = DateTime.UtcNow - latest.LastWriteTimeUtc;
            return new DiagnosticItemDto
            {
                Status = age > TimeSpan.FromHours(maxAgeHours)
                    ? DiagnosticStatuses.Degraded
                    : DiagnosticStatuses.Healthy,
                Value = latest.Length,
                ObservedAt = latest.LastWriteTimeUtc,
                Message = $"最終バックアップ {latest.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC ({FormatBytes(latest.Length)})",
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticItemDto
            {
                Status = DiagnosticStatuses.Degraded,
                Message = $"バックアップ診断失敗: {SafeMessage(ex)}",
            };
        }
    }

    private DiagnosticItemDto CheckCertificate()
    {
        var path = _configuration["Kestrel:Certificates:Default:Path"];
        if (string.IsNullOrWhiteSpace(path))
            return new DiagnosticItemDto
            {
                Status = DiagnosticStatuses.NotConfigured,
                Message = "HTTPS証明書はアプリ設定上未指定です（HTTP利用は許可されています）。",
            };

        try
        {
            if (!File.Exists(path))
                return new DiagnosticItemDto
                {
                    Status = DiagnosticStatuses.Unhealthy,
                    Message = "設定された証明書ファイルが見つかりません。",
                };
            var password = _configuration["Kestrel:Certificates:Default:Password"];
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(path, password);
            var remaining = certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow;
            var warningDays = Math.Max(1, _configuration.GetValue<int?>("Operations:CertificateWarningDays") ?? 30);
            return new DiagnosticItemDto
            {
                Status = remaining <= TimeSpan.Zero
                    ? DiagnosticStatuses.Unhealthy
                    : remaining < TimeSpan.FromDays(warningDays)
                        ? DiagnosticStatuses.Degraded
                        : DiagnosticStatuses.Healthy,
                ObservedAt = certificate.NotAfter.ToUniversalTime(),
                Message = $"有効期限 {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC",
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticItemDto
            {
                Status = DiagnosticStatuses.Unhealthy,
                Message = $"証明書診断失敗: {SafeMessage(ex)}",
            };
        }
    }

    private async Task<List<NodeDiagnosticDto>> CheckNodesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nodes = await _db.ExecutionNodes.AsNoTracking()
            .OrderBy(node => node.Id)
            .ToListAsync(ct);
        return nodes.Select(node => new NodeDiagnosticDto
        {
            Id = node.Id,
            Name = node.Name,
            NodeType = node.NodeType,
            IsActive = node.IsActive,
            Status = !node.IsActive
                    ? DiagnosticStatuses.NotConfigured
                    : node.NodeType == NodeTypes.Direct
                        ? DiagnosticStatuses.Healthy
                        : node.HealthStatus == HealthStatuses.Healthy
                            ? DiagnosticStatuses.Healthy
                            : node.HealthStatus == HealthStatuses.Unhealthy
                                ? DiagnosticStatuses.Unhealthy
                                : DiagnosticStatuses.Degraded,
            LastHeartbeatAt = node.LastHeartbeatAt,
            EndpointScheme = node.Endpoint == null
                    ? null
                    : node.Endpoint.StartsWith("https://") ? "HTTPS"
                    : node.Endpoint.StartsWith("http://") ? "HTTP" : "不明",
            MaxConcurrency = node.MaxConcurrency,
            Message = !node.IsActive
                    ? "無効"
                    : node.NodeType == NodeTypes.Direct
                        ? "中央サーバーから直接実行"
                        : node.LastHeartbeatAt == null
                            ? "ハートビート未受信"
                            : "最終heartbeatから " + Math.Max(0, (long)(now - node.LastHeartbeatAt.Value).TotalSeconds) + " 秒",
        })
            .ToList();
    }

    internal static string CalculateOverallStatus(OperationalStatusDto result)
    {
        if (result.Database.Status == DiagnosticStatuses.Unhealthy ||
            result.AuditOutbox.Status == DiagnosticStatuses.Unhealthy ||
            result.Disk.Status == DiagnosticStatuses.Unhealthy ||
            result.Certificate.Status == DiagnosticStatuses.Unhealthy)
            return DiagnosticStatuses.Unhealthy;

        var infrastructure = new[]
        {
            result.AuditOutbox.Status, result.Disk.Status, result.Backup.Status, result.Certificate.Status,
        };
        if (infrastructure.Any(status => status == DiagnosticStatuses.Degraded) ||
            result.Nodes.Any(node => node.IsActive && node.Status != DiagnosticStatuses.Healthy))
            return DiagnosticStatuses.Degraded;
        return DiagnosticStatuses.Healthy;
    }

    private static string SafeMessage(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "アクセス権がありません。",
        IOException => ex.Message,
        SqliteException => ex.Message,
        _ => ex.Message,
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
