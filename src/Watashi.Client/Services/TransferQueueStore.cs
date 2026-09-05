using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Watashi.Client.Services;

public static class TransferDirections
{
    public const string Upload = "upload";
    public const string Download = "download";
}

public static class TransferJobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Canceling = "canceling";
    public const string RetryWaiting = "retry_waiting";
    public const string Paused = "paused";
    public const string MaintenanceWaiting = "maintenance_waiting";
    public const string ConflictWaiting = "conflict_waiting";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
    public const string Skipped = "skipped";
}

public static class TransferConflictPolicies
{
    public const string Ask = "ask";
    public const string Overwrite = "overwrite";
    public const string Skip = "skip";
    public const string Rename = "rename";
}

/// <summary>
/// 再起動後も復元できる転送ジョブ。資格情報やtokenは保存せず、再実行時に現在のSessionManagerと
/// サーバー側権限を必ず使う。Serverのupload session IDも秘密として扱わず所有者認可を必須にする。
/// </summary>
public class TransferJobRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Direction { get; set; } = TransferDirections.Upload;
    public string LocalPath { get; set; } = string.Empty;
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public string RemotePath { get; set; } = "/";
    public long TotalBytes { get; set; }
    public long BytesTransferred { get; set; }
    public string State { get; set; } = TransferJobStates.Queued;
    public string ConflictPolicy { get; set; } = TransferConflictPolicies.Ask;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? MaintenanceResumeState { get; set; }
    public long? ConflictDestinationSize { get; set; }
    public DateTime? ConflictDestinationModifiedUtc { get; set; }
    public string? ServerSessionId { get; set; }
    public int ServerSessionGeneration { get; set; }
    public string? ContentSha256 { get; set; }
    public string? RemoteETag { get; set; }
    public DateTime? SourceLastWriteUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>transfer-queue.json を同一ディレクトリ内の一時ファイルから原子的に置換する。</summary>
public class TransferQueueStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TransferQueueStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Brand.Id,
            "transfer-queue.json");
    }

    /// <summary>同じWindows利用者がWatashiアカウントを切り替えても、別ユーザーのジョブを実行しない。</summary>
    public TransferQueueStore(int userId)
        : this(GetUserQueuePath(userId))
    {
    }

    public TransferQueueStore(int userId, string serverUrl)
        : this(GetUserQueuePath(userId, serverUrl))
    {
    }

    public static string GetUserQueuePath(int userId)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Brand.Id,
            $"transfer-queue-user-{userId}.json");
    }

    public static string GetUserQueuePath(int userId, string serverUrl)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("転送キューの接続先URLが不正です。", nameof(serverUrl));
        // URIのscheme/hostは大文字小文字を区別しないが、pathはサーバーによって
        // case-sensitiveである。接続先tenantの異なるキューを混在させない。
        var authority = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        var identity = authority + uri.AbsolutePath.TrimEnd('/');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Brand.Id,
            $"transfer-queue-{hash}-user-{userId}.json");
    }

    public virtual async Task<List<TransferJobRecord>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return new List<TransferJobRecord>();
            try
            {
                await using var stream = new FileStream(
                    _path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 64 * 1024, useAsync: true);
                var jobs = await JsonSerializer.DeserializeAsync<List<TransferJobRecord>>(
                    stream, JsonOptions, ct) ?? new List<TransferJobRecord>();
                return NormalizeLoadedJobs(jobs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                if (!PreserveCorruptFile())
                    throw new IOException(
                        "破損した転送キューを安全に退避できないため、元ファイルを上書きせず停止しました。",
                        ex);
                return new List<TransferJobRecord>();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 同じユーザー/接続先のキューを別プロセスが同時実行しないためのOSファイルlease。
    /// 呼び出し側はキュー稼働中ずっと返却値を保持し、終了時にDisposeする。
    /// </summary>
    public virtual IDisposable AcquireExclusiveLease()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        try
        {
            return new FileStream(
                _path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new IOException("同じ利用者・接続先の転送キューが別のWatashiウィンドウで実行中です。", ex);
        }
    }

    public virtual async Task SaveAsync(IReadOnlyCollection<TransferJobRecord> jobs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        await _gate.WaitAsync(ct);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, jobs, JsonOptions, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
            _gate.Release();
        }
    }

    internal static List<TransferJobRecord> NormalizeLoadedJobs(IEnumerable<TransferJobRecord> jobs)
    {
        var result = new List<TransferJobRecord>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (job is null || !Guid.TryParseExact(job.Id, "N", out _) || !ids.Add(job.Id)) continue;
            if (job.Direction is not (TransferDirections.Upload or TransferDirections.Download)) continue;
            if (job.HostId <= 0 || job.ShareId <= 0 || string.IsNullOrWhiteSpace(job.LocalPath) ||
                string.IsNullOrWhiteSpace(job.RemotePath))
                continue;
            job.TotalBytes = Math.Max(0, job.TotalBytes);
            job.BytesTransferred = Math.Clamp(job.BytesTransferred, 0, job.TotalBytes);
            job.ServerSessionGeneration = Math.Max(0, job.ServerSessionGeneration);
            if (job.State == TransferJobStates.Running && job.MaintenanceResumeState == TransferJobStates.Queued)
            {
                job.State = TransferJobStates.MaintenanceWaiting;
                job.LastError = "メンテナンス終了の確認を待っています。復旧確認後に再開します。";
            }
            else if (job.State is TransferJobStates.Running or TransferJobStates.Canceling)
            {
                job.State = TransferJobStates.Paused;
                job.LastError = "アプリ終了前に完了しなかったため一時停止しました。再開してください。";
            }
            else if (job.State == TransferJobStates.RetryWaiting && !job.NextAttemptAtUtc.HasValue)
            {
                job.State = TransferJobStates.Queued;
                job.LastError = "自動再試行時刻が失われたため、待機列へ戻しました。";
            }
            if (!KnownState(job.State)) job.State = TransferJobStates.Paused;
            if (job.State == TransferJobStates.MaintenanceWaiting &&
                job.MaintenanceResumeState is not (TransferJobStates.Queued or TransferJobStates.RetryWaiting))
                job.MaintenanceResumeState = TransferJobStates.Queued;
            if (!KnownConflictPolicy(job.ConflictPolicy)) job.ConflictPolicy = TransferConflictPolicies.Ask;
            result.Add(job);
        }
        return result;
    }

    private bool PreserveCorruptFile()
    {
        try
        {
            var preserved = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            File.Move(_path, preserved, overwrite: false);
            return true;
        }
        catch
        {
            // 読み出せない元ファイルは削除しない。次回も復旧を試せる状態を保つ。
            return false;
        }
    }

    private static bool KnownState(string? state) => state is
        TransferJobStates.Queued or TransferJobStates.Running or TransferJobStates.RetryWaiting or
        TransferJobStates.Canceling or TransferJobStates.Paused or TransferJobStates.Completed or TransferJobStates.Failed or
        TransferJobStates.MaintenanceWaiting or TransferJobStates.ConflictWaiting or
        TransferJobStates.Canceled or TransferJobStates.Skipped;

    private static bool KnownConflictPolicy(string? policy) => policy is
        TransferConflictPolicies.Ask or TransferConflictPolicies.Overwrite or
        TransferConflictPolicies.Skip or TransferConflictPolicies.Rename;
}
