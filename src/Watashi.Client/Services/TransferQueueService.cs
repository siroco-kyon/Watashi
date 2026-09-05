using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Watashi.Shared.Cifs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Client.Services;

public sealed record UploadQueueRequest(
    string LocalPath,
    int HostId,
    int ShareId,
    string RemotePath,
    string ConflictPolicy);

public sealed record DownloadQueueRequest(
    int HostId,
    int ShareId,
    string RemotePath,
    string LocalPath,
    long ExpectedSize,
    string ConflictPolicy);

/// <summary>
/// 永続転送キュー。転送は複数ワーカーで実行でき、アプリ終了や通信断後も確定済みoffsetから再開する。
/// 資格情報は保持せず、各HTTP要求で現在のログインとサーバー側権限を再評価する。
/// </summary>
public sealed class TransferQueueService : IAsyncDisposable
{
    public const int ChunkSize = 8 * 1024 * 1024;
    internal const int MaxTerminalHistory = 1000;
    internal static readonly TimeSpan CompletedHistoryRetention = TimeSpan.FromDays(30);
    internal static readonly TimeSpan FailedHistoryRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan RemoteCancelTimeout = TimeSpan.FromSeconds(10);

    private readonly TransferQueueStore _store;
    private readonly ITransferProtocol _protocol;
    private readonly int _maxConcurrent;
    private readonly Func<int, TimeSpan> _retryDelayFactory;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<TransferJobRecord> _jobs = new();
    private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _activeResourceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _workerTasks = new();
    private readonly object _publicationLock = new();
    private long _nextPublicationRevision;
    private long _lastPublishedRevision;
    private Task? _pumpTask;
    private IDisposable? _exclusiveLease;
    private bool _initialized;
    private bool _disposing;
    private bool _disposed;
    private volatile bool _maintenanceBlocked;
    private bool _maintenanceStateKnown;

    public bool IsMaintenanceBlocked => _maintenanceBlocked;

    public event Action<IReadOnlyList<TransferJobRecord>>? QueueChanged;
    public event Action<string>? QueueError;

    public TransferQueueService(
        TransferQueueStore store,
        ITransferProtocol protocol,
        int maxConcurrent = 2,
        Func<int, TimeSpan>? retryDelayFactory = null,
        Func<DateTime>? utcNow = null)
    {
        _store = store;
        _protocol = protocol;
        _maxConcurrent = Math.Clamp(maxConcurrent, 1, 4);
        _retryDelayFactory = retryDelayFactory ?? DefaultRetryDelay;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try { await InitializeCoreAsync(ct); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        QueuePublication publication;
        await _mutex.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (!_initialized)
            {
                _exclusiveLease = _store.AcquireExclusiveLease();
                try
                {
                    _jobs.AddRange(await _store.LoadAsync(ct));
                    if (!_maintenanceStateKnown && _jobs.Any(job => job.State == TransferJobStates.MaintenanceWaiting))
                        _maintenanceBlocked = true;
                    var requiresMaintenanceSave = _jobs.Any(job => job.State == TransferJobStates.MaintenanceWaiting ||
                        (_maintenanceBlocked && job.State is TransferJobStates.Queued or TransferJobStates.RetryWaiting));
                    ApplyMaintenanceLocked();
                    if (requiresMaintenanceSave) await _store.SaveAsync(_jobs, ct);
                }
                catch
                {
                    _jobs.Clear();
                    _exclusiveLease.Dispose();
                    _exclusiveLease = null;
                    throw;
                }
                _initialized = true;
                _pumpTask = Task.Run(PumpAsync);
            }
            publication = CapturePublicationLocked();
        }
        finally { _mutex.Release(); }

        Publish(publication);
        await CleanupExpiredHistoryBestEffortAsync(ct);
        foreach (var retry in publication.Jobs.Where(job => job.State == TransferJobStates.RetryWaiting))
            ScheduleRetryWake(retry.NextAttemptAtUtc);
        SignalPump();
    }

    public async Task<IReadOnlyList<TransferJobRecord>> SnapshotAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct);
        try { return SnapshotLocked(); }
        finally { _mutex.Release(); }
    }

    /// <summary>進行中の要求は中断せず、確定済みチャンクを保存した境界で保留する。</summary>
    public async Task SetMaintenanceAsync(bool blocked, CancellationToken ct = default)
    {
        QueuePublication publication;
        await _mutex.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var before = CloneAllLocked();
            _maintenanceStateKnown = true;
            _maintenanceBlocked = blocked;
            ApplyMaintenanceLocked();
            try
            {
                if (_initialized) await SaveOrRollbackLockedAsync(before, ct);
            }
            catch
            {
                // 保存できない場合に復旧を宣言してpumpを開けない。
                _maintenanceBlocked = true;
                throw;
            }
            publication = CapturePublicationLocked();
        }
        finally { _mutex.Release(); }
        Publish(publication);
        if (!blocked)
        {
            foreach (var retry in publication.Jobs.Where(job => job.State == TransferJobStates.RetryWaiting))
                ScheduleRetryWake(retry.NextAttemptAtUtc);
            SignalPump();
        }
    }

    private void ApplyMaintenanceLocked()
    {
        foreach (var job in _jobs)
        {
            if (_maintenanceBlocked && job.State is TransferJobStates.Queued or TransferJobStates.RetryWaiting)
            {
                job.MaintenanceResumeState = job.State;
                job.State = TransferJobStates.MaintenanceWaiting;
                job.LastError = "メンテナンス終了の確認を待っています。転送の途中データは保持しています。";
            }
            else if (!_maintenanceBlocked && job.State == TransferJobStates.MaintenanceWaiting)
            {
                job.State = job.MaintenanceResumeState == TransferJobStates.RetryWaiting
                    ? TransferJobStates.RetryWaiting : TransferJobStates.Queued;
                job.MaintenanceResumeState = null;
                job.LastError = null;
            }
            else if (job.State == TransferJobStates.Running)
                job.MaintenanceResumeState = _maintenanceBlocked ? TransferJobStates.Queued : null;
        }
    }

    private void CheckMaintenanceBoundary(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_maintenanceBlocked) throw new MaintenanceHoldException();
    }

    public async Task<string> EnqueueUploadAsync(
        string localPath,
        int hostId,
        int shareId,
        string remotePath,
        string conflictPolicy,
        CancellationToken ct = default)
    {
        var ids = await EnqueueUploadsAsync(
            new[] { new UploadQueueRequest(localPath, hostId, shareId, remotePath, conflictPolicy) }, ct);
        return ids[0];
    }

    public async Task<IReadOnlyList<string>> EnqueueUploadsAsync(
        IEnumerable<UploadQueueRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var jobs = requests.Select(request =>
        {
            var fullPath = Path.GetFullPath(request.LocalPath);
            var info = new FileInfo(fullPath);
            if (!info.Exists)
                throw new FileNotFoundException("アップロード元ファイルが見つかりません。", fullPath);
            var job = NewJob(
                TransferDirections.Upload, fullPath, request.HostId, request.ShareId,
                request.RemotePath, request.ConflictPolicy);
            job.TotalBytes = info.Length;
            job.SourceLastWriteUtc = info.LastWriteTimeUtc;
            return job;
        }).ToArray();
        await AddJobsAsync(jobs, ct);
        return jobs.Select(job => job.Id).ToArray();
    }

    public async Task<string> EnqueueDownloadAsync(
        int hostId,
        int shareId,
        string remotePath,
        string localPath,
        long expectedSize,
        string conflictPolicy,
        CancellationToken ct = default)
    {
        var ids = await EnqueueDownloadsAsync(
            new[] { new DownloadQueueRequest(hostId, shareId, remotePath, localPath, expectedSize, conflictPolicy) },
            ct);
        return ids[0];
    }

    public async Task<IReadOnlyList<string>> EnqueueDownloadsAsync(
        IEnumerable<DownloadQueueRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var jobs = requests.Select(request =>
        {
            var fullPath = Path.GetFullPath(request.LocalPath);
            var job = NewJob(
                TransferDirections.Download, fullPath, request.HostId, request.ShareId,
                request.RemotePath, request.ConflictPolicy);
            job.TotalBytes = Math.Max(0, request.ExpectedSize);
            return job;
        }).ToArray();
        await AddJobsAsync(jobs, ct);
        return jobs.Select(job => job.Id).ToArray();
    }

    public Task RetryFailedAsync(CancellationToken ct = default)
        => MutateAsync(
            job => job.State == TransferJobStates.Failed,
            job =>
            {
                job.State = TransferJobStates.Queued;
                job.LastError = null;
                job.NextAttemptAtUtc = null;
            }, signal: true, ct);

    public Task RetryAsync(string jobId, CancellationToken ct = default)
        => MutateAsync(
            job => IdEquals(job, jobId) && job.State == TransferJobStates.Failed,
            job =>
            {
                job.State = TransferJobStates.Queued;
                job.LastError = null;
                job.NextAttemptAtUtc = null;
            }, signal: true, ct);

    public Task ResumeAsync(string jobId, CancellationToken ct = default)
        => MutateAsync(
            job => IdEquals(job, jobId) &&
                   (job.State is TransferJobStates.Paused or TransferJobStates.Canceled) &&
                   !_active.ContainsKey(job.Id),
            job =>
            {
                job.State = TransferJobStates.Queued;
                job.LastError = null;
            }, signal: true, ct);

    public async Task ResolveConflictAndRetryAsync(
        string jobId,
        string conflictPolicy,
        CancellationToken ct = default)
    {
        if (conflictPolicy is not (TransferConflictPolicies.Overwrite or TransferConflictPolicies.Skip or
            TransferConflictPolicies.Rename))
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));

        TransferJobRecord? selected = null;
        await _mutex.WaitAsync(ct);
        try
        {
            if (_maintenanceBlocked) throw new InvalidOperationException("メンテナンス中は競合の再試行を開始できません。");
            var job = _jobs.FirstOrDefault(j => IdEquals(j, jobId) &&
                j.State == TransferJobStates.ConflictWaiting &&
                !_active.ContainsKey(j.Id));
            if (job is null) return;
            selected = Clone(job);
        }
        finally { _mutex.Release(); }

        if (Guid.TryParse(selected.ServerSessionId, out var sessionId))
        {
            var response = await CancelUploadSessionBoundedAsync(sessionId, ct);
            EnsureUploadSessionMatches(selected, response, sessionId);
            if (response.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                await SetTerminalAsync(jobId, TransferJobStates.Completed, null, ct);
                await UpdateAsync(jobId, job => job.RemoteETag = response.ETag, ct);
                return;
            }
            if (!IsCanceledUploadSession(response.Status))
                throw new IOException($"サーバー側の一時転送を中止できませんでした ({response.Status})。");
        }
        await MutateAsync(job => IdEquals(job, jobId) &&
            job.State == TransferJobStates.ConflictWaiting &&
            job.ServerSessionGeneration == selected.ServerSessionGeneration &&
            job.ServerSessionId == selected.ServerSessionId, job =>
        {
            job.ConflictPolicy = conflictPolicy;
            job.ServerSessionId = null;
            job.ServerSessionGeneration++;
            job.BytesTransferred = 0;
            job.State = TransferJobStates.Queued;
            job.LastError = null;
            job.ConflictDestinationSize = null;
            job.ConflictDestinationModifiedUtc = null;
        }, signal: true, ct);
    }

    public async Task CancelAsync(string jobId, CancellationToken ct = default)
    {
        CancellationTokenSource? active = null;
        Guid? sessionId = null;
        QueuePublication publication;
        await _mutex.WaitAsync(ct);
        try
        {
            var job = _jobs.FirstOrDefault(j => IdEquals(j, jobId));
            if (job is null || IsTerminal(job.State) || job.State == TransferJobStates.Canceling) return;
            var before = CloneAllLocked();
            _active.TryGetValue(job.Id, out active);
            if (Guid.TryParse(job.ServerSessionId, out var parsed)) sessionId = parsed;
            var requiresCleanup = active is not null || sessionId.HasValue;
            job.State = requiresCleanup ? TransferJobStates.Canceling : TransferJobStates.Canceled;
            job.LastError = requiresCleanup
                ? "転送の中止処理を完了しています。"
                : "利用者が転送をキャンセルしました。";
            job.UpdatedAt = DateTime.UtcNow;
            await SaveOrRollbackLockedAsync(before, ct);
            publication = CapturePublicationLocked();
        }
        finally { _mutex.Release(); }
        active?.Cancel();
        Publish(publication);

        if (active is null && sessionId.HasValue)
        {
            try
            {
                var completed = await ReconcileCanceledRemoteSessionAsync(jobId, sessionId.Value, ct);
                if (!completed)
                    await SetTerminalAsync(
                        jobId, TransferJobStates.Canceled, "利用者が転送をキャンセルしました。", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await PersistTerminalSafelyAsync(
                    jobId, TransferJobStates.Paused,
                    "中止処理が完了する前に操作が取り消されました。再度中止してください。");
                throw;
            }
            catch (Exception ex)
            {
                await PersistTerminalSafelyAsync(
                    jobId, TransferJobStates.Paused,
                    "中止状態を保存できませんでした。再度中止してください: " + ex.Message);
                PublishError("転送の中止処理を完了できませんでした: " + ex.Message);
            }
        }
    }

    public async Task CancelAllAsync(CancellationToken ct = default)
    {
        var activeCancellations = new List<CancellationTokenSource>();
        var inactiveSessions = new List<TransferJobRecord>();
        QueuePublication? publication = null;
        await _mutex.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var candidates = _jobs
                .Where(job => !IsTerminal(job.State) && job.State != TransferJobStates.Canceling)
                .ToArray();
            if (candidates.Length == 0) return;
            var before = CloneAllLocked();
            foreach (var job in candidates)
            {
                if (_active.TryGetValue(job.Id, out var active))
                {
                    activeCancellations.Add(active);
                    job.State = TransferJobStates.Canceling;
                    job.LastError = "転送の中止処理を完了しています。";
                }
                else if (Guid.TryParse(job.ServerSessionId, out _))
                {
                    inactiveSessions.Add(Clone(job));
                    job.State = TransferJobStates.Canceling;
                    job.LastError = "サーバー側の中止処理を確認しています。";
                }
                else
                {
                    job.State = TransferJobStates.Canceled;
                    job.LastError = "利用者が転送をキャンセルしました。";
                }
                job.UpdatedAt = DateTime.UtcNow;
            }
            await SaveOrRollbackLockedAsync(before, ct);
            publication = CapturePublicationLocked();
        }
        finally { _mutex.Release(); }
        foreach (var active in activeCancellations) active.Cancel();
        if (publication is not null) Publish(publication);

        if (inactiveSessions.Count == 0) return;
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<BatchCancelOutcome>();
        await Parallel.ForEachAsync(
            inactiveSessions,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (job, token) => outcomes.Add(await CancelInactiveSessionAsync(job, token)));

        QueuePublication? finalPublication = null;
        await _mutex.WaitAsync(ct);
        try
        {
            var before = CloneAllLocked();
            var changed = false;
            foreach (var outcome in outcomes)
            {
                var job = _jobs.FirstOrDefault(candidate =>
                    IdEquals(candidate, outcome.JobId) && candidate.State == TransferJobStates.Canceling);
                if (job is null) continue;
                changed = true;
                switch (outcome.Result)
                {
                    case RemoteCancelResult.Completed:
                        job.State = TransferJobStates.Completed;
                        job.BytesTransferred = job.TotalBytes;
                        job.RemoteETag = outcome.ETag;
                        job.CompletedAtUtc = DateTime.UtcNow;
                        job.LastError = null;
                        break;
                    case RemoteCancelResult.Canceled:
                        job.State = TransferJobStates.Canceled;
                        job.ServerSessionId = null;
                        job.ServerSessionGeneration++;
                        job.BytesTransferred = 0;
                        job.LastError = "利用者が転送をキャンセルしました。";
                        break;
                    default:
                        job.State = TransferJobStates.Paused;
                        job.LastError = outcome.Error ??
                            "サーバー側の中止結果を確認できませんでした。再度中止してください。";
                        break;
                }
                job.UpdatedAt = DateTime.UtcNow;
            }
            if (changed)
            {
                await SaveOrRollbackLockedAsync(before, ct);
                finalPublication = CapturePublicationLocked();
            }
        }
        finally { _mutex.Release(); }
        if (finalPublication is not null) Publish(finalPublication);
    }

    public async Task RemoveFinishedAsync(CancellationToken ct = default)
    {
        TransferJobRecord[] removable;
        await _mutex.WaitAsync(ct);
        try
        {
            removable = _jobs.Where(job => job.State is TransferJobStates.Completed or
                TransferJobStates.Failed or TransferJobStates.Canceled or TransferJobStates.Skipped)
                .Select(Clone).ToArray();
        }
        finally { _mutex.Release(); }

        await RemoveJobsAsync(removable, ct);
    }

    private async Task CleanupExpiredHistoryAsync(CancellationToken ct = default)
    {
        TransferJobRecord[] removable;
        await _mutex.WaitAsync(ct);
        try
        {
            var now = _utcNow();
            var terminal = _jobs.Where(job => IsTerminal(job.State)).ToList();
            var expiredIds = terminal
                .Where(job => IsExpiredHistory(job, now))
                .Select(job => job.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retained = terminal
                .Where(job => !expiredIds.Contains(job.Id))
                .OrderByDescending(HistoryTimestamp)
                .ToList();
            foreach (var excess in retained.Skip(MaxTerminalHistory))
                expiredIds.Add(excess.Id);
            removable = terminal
                .Where(job => expiredIds.Contains(job.Id))
                .Select(Clone)
                .ToArray();
        }
        finally { _mutex.Release(); }

        await RemoveJobsAsync(removable, ct);
    }

    private async Task CleanupExpiredHistoryBestEffortAsync(CancellationToken ct)
    {
        try { await CleanupExpiredHistoryAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { PublishError("転送履歴の自動整理に失敗しました: " + ex.Message); }
    }

    private async Task RemoveJobsAsync(IReadOnlyCollection<TransferJobRecord> removable, CancellationToken ct)
    {
        if (removable.Count == 0) return;
        await Parallel.ForEachAsync(
            removable.Where(job => job.Direction == TransferDirections.Upload &&
                                   Guid.TryParse(job.ServerSessionId, out _)),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (job, token) => await CleanupRemovedUploadSessionBestEffortAsync(job, token));
        foreach (var job in removable.Where(x => x.Direction == TransferDirections.Download))
            DeletePartialBestEffort(job.LocalPath, job.Id);
        await RemoveAsync(job => removable.Any(x => IdEquals(job, x.Id)), ct);
    }

    private static bool IsExpiredHistory(TransferJobRecord job, DateTime now)
    {
        var retention = job.State is TransferJobStates.Completed or TransferJobStates.Skipped
            ? CompletedHistoryRetention
            : FailedHistoryRetention;
        return HistoryTimestamp(job) <= now - retention;
    }

    private static DateTime HistoryTimestamp(TransferJobRecord job)
        => job.CompletedAtUtc ?? job.UpdatedAt;

    private async Task AddJobsAsync(IReadOnlyList<TransferJobRecord> jobs, CancellationToken ct)
    {
        if (jobs.Count == 0) return;
        if (jobs.Select(job => job.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != jobs.Count)
            throw new InvalidOperationException("転送IDが重複しています。");
        QueuePublication publication;
        await _mutex.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            if (_maintenanceBlocked) throw new InvalidOperationException("メンテナンス中は転送を追加できません。");
            if (jobs.Any(job => _jobs.Any(existing => IdEquals(existing, job.Id))))
                throw new InvalidOperationException("転送IDが重複しています。");
            var before = CloneAllLocked();
            _jobs.AddRange(jobs);
            await SaveOrRollbackLockedAsync(before, ct);
            publication = CapturePublicationLocked();
        }
        finally { _mutex.Release(); }
        Publish(publication);
        SignalPump();
    }

    private async Task PumpAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_lifetime.Token);
                while (true)
                {
                    string? id = null;
                    QueuePublication? publication = null;
                    await _mutex.WaitAsync(_lifetime.Token);
                    try
                    {
                        if (_maintenanceBlocked || _active.Count >= _maxConcurrent) break;
                        var activeResourceKeys = _activeResourceKeys.Values
                            .SelectMany(keys => keys)
                            .Concat(_jobs.Where(j => j.State == TransferJobStates.Canceling).Select(ResourceKey))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var now = DateTime.UtcNow;
                        var job = _jobs.FirstOrDefault(j =>
                            (j.State == TransferJobStates.Queued ||
                             (j.State == TransferJobStates.RetryWaiting &&
                              (!j.NextAttemptAtUtc.HasValue || j.NextAttemptAtUtc <= now))) &&
                            !_active.ContainsKey(j.Id) &&
                            !activeResourceKeys.Contains(ResourceKey(j)));
                        if (job is null) break;
                        var before = CloneAllLocked();
                        var cts = new CancellationTokenSource();
                        try
                        {
                            job.State = TransferJobStates.Running;
                            job.AttemptCount++;
                            job.LastError = null;
                            job.UpdatedAt = DateTime.UtcNow;
                            await SaveOrRollbackLockedAsync(before, _lifetime.Token);
                            _active.Add(job.Id, cts);
                            _activeResourceKeys.Add(job.Id,
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ResourceKey(job) });
                        }
                        catch
                        {
                            _active.Remove(job.Id);
                            _activeResourceKeys.Remove(job.Id);
                            cts.Dispose();
                            throw;
                        }
                        publication = CapturePublicationLocked();
                        id = job.Id;
                    }
                    finally { _mutex.Release(); }
                    if (publication is not null) Publish(publication);
                    if (id is null) break;

                    var task = RunTrackedAsync(id);
                    lock (_workerTasks) _workerTasks.Add(task);
                    _ = task.ContinueWith(
                        completed => { lock (_workerTasks) _workerTasks.Remove(completed); },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                PublishError("転送キューを開始できませんでした。保存先を確認してください: " + ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                SignalPump();
            }
        }
    }

    private async Task RunTrackedAsync(string jobId)
    {
        CancellationTokenSource jobCts;
        await _mutex.WaitAsync();
        try
        {
            if (!_active.TryGetValue(jobId, out jobCts!)) return;
        }
        finally { _mutex.Release(); }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, jobCts.Token);
        try
        {
            var outcome = await ProcessAsync(jobId, linked.Token);
            await PersistTerminalSafelyAsync(jobId, outcome, null);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await HandleCancellationAsync(jobId);
        }
        catch (Exception ex) when (ex is MaintenanceHoldException ||
                                   ex is ApiException { StatusCode: HttpStatusCode.ServiceUnavailable, ErrorCode: "maintenance" })
        {
            if (ex is ApiException)
            {
                try { await SetMaintenanceAsync(true); }
                catch (Exception saveError) { PublishError("メンテナンスの保留状態を保存できませんでした: " + saveError.Message); }
            }
            if (linked.IsCancellationRequested)
                await HandleCancellationAsync(jobId);
            else
                await HoldForMaintenanceAsync(jobId);
        }
        catch (TransferConflictException ex)
        {
            if (linked.IsCancellationRequested) await HandleCancellationAsync(jobId);
            else await PersistTerminalSafelyAsync(jobId, TransferJobStates.ConflictWaiting, ex.Message);
        }
        catch (Exception ex)
        {
            if (linked.IsCancellationRequested) await HandleCancellationAsync(jobId);
            else if (!await ScheduleAutomaticRetrySafelyAsync(jobId, ex))
                await PersistTerminalSafelyAsync(jobId, TransferJobStates.Failed, FriendlyError(ex));
        }
        finally
        {
            bool cancelPending;
            await _mutex.WaitAsync();
            try
            {
                if (_active.Remove(jobId, out var cts)) cts.Dispose();
                _activeResourceKeys.Remove(jobId);
                cancelPending = _jobs.Any(job => IdEquals(job, jobId) && job.State == TransferJobStates.Canceling);
            }
            finally { _mutex.Release(); }
            if (cancelPending) await HandleCancellationAsync(jobId);
            SignalPump();
        }
    }

    private async Task HandleCancellationAsync(string jobId)
    {
        if (_lifetime.IsCancellationRequested && _maintenanceBlocked &&
            (await FindSnapshotAsync(jobId, CancellationToken.None))?.State == TransferJobStates.Running)
        {
            await HoldForMaintenanceAsync(jobId);
            return;
        }
        var state = _lifetime.IsCancellationRequested ? TransferJobStates.Paused : TransferJobStates.Canceled;
        var reason = state == TransferJobStates.Paused
            ? "アプリ終了のため一時停止しました。"
            : "利用者が転送をキャンセルしました。";
        if (state == TransferJobStates.Canceled)
        {
            var cancelResult = await CancelRemoteSessionBestEffortAsync(jobId);
            if (cancelResult == RemoteCancelResult.Completed)
            {
                state = TransferJobStates.Completed;
                reason = null;
            }
            else if (cancelResult == RemoteCancelResult.Uncertain)
            {
                state = TransferJobStates.Paused;
                reason = "サーバー側の中止結果を確認できませんでした。再度中止してください。";
            }
        }
        await PersistTerminalSafelyAsync(jobId, state, reason);
    }

    private async Task HoldForMaintenanceAsync(string jobId)
    {
        try
        {
            await MutateAsync(job => IdEquals(job, jobId) && job.State == TransferJobStates.Running, job =>
            {
                job.AttemptCount = Math.Max(0, job.AttemptCount - 1);
                job.State = _maintenanceBlocked ? TransferJobStates.MaintenanceWaiting : TransferJobStates.Queued;
                job.MaintenanceResumeState = _maintenanceBlocked ? TransferJobStates.Queued : null;
                job.LastError = _maintenanceBlocked ? "メンテナンス終了の確認を待っています。途中から再開します。" : null;
                job.NextAttemptAtUtc = null;
            }, signal: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await PersistTerminalSafelyAsync(jobId, TransferJobStates.Paused,
                "メンテナンスの保留状態を保存できませんでした。復旧後に再開してください: " + ex.Message);
        }
    }

    private async Task<string> ProcessAsync(string jobId, CancellationToken ct)
    {
        CheckMaintenanceBoundary(ct);
        var job = await FindSnapshotAsync(jobId, ct)
            ?? throw new InvalidOperationException("転送ジョブが見つかりません。");
        try
        {
            return job.Direction == TransferDirections.Upload
                ? await ProcessUploadAsync(job, ct)
                : await ProcessDownloadAsync(job, ct);
        }
        catch (ApiException ex) when (job.Direction == TransferDirections.Upload &&
            ex.StatusCode == HttpStatusCode.Conflict && ex.ErrorCode is "target_exists" or "target_conflict")
        {
            // Only a confirmed destination-name conflict exposes overwrite. Other 409s
            // (offset, checksum, session identity) remain ordinary failures.
            var current = await FindSnapshotAsync(jobId, ct) ?? job;
            var metadata = await _protocol.GetDownloadMetadataV2Async(current.HostId, current.ShareId, current.RemotePath, ct);
            if (!metadata.Exists || metadata.Type != "file") throw;
            await UpdateAsync(jobId, item =>
            {
                item.ConflictDestinationSize = metadata.Size;
                item.ConflictDestinationModifiedUtc = metadata.ModifiedAtUtc;
            }, ct);
            throw new TransferConflictException("転送先に同名ファイルが作成されました。上書き・スキップ・別名を選択してください。");
        }
    }

    private async Task<string> ProcessUploadAsync(TransferJobRecord job, CancellationToken ct)
    {
        var file = new FileInfo(job.LocalPath);
        if (!file.Exists) throw new FileNotFoundException("アップロード元ファイルが見つかりません。", job.LocalPath);
        if (file.Length != job.TotalBytes ||
            (job.SourceLastWriteUtc.HasValue && file.LastWriteTimeUtc != job.SourceLastWriteUtc.Value))
            throw new IOException("待機中にアップロード元ファイルが変更されました。新しいジョブとして追加し直してください。");

        if (string.IsNullOrWhiteSpace(job.ContentSha256))
        {
            var sha = await ComputeFileSha256Async(job.LocalPath, ct);
            file.Refresh();
            if (!file.Exists || file.Length != job.TotalBytes ||
                (job.SourceLastWriteUtc.HasValue && file.LastWriteTimeUtc != job.SourceLastWriteUtc.Value))
                throw new IOException("ハッシュ計算中にアップロード元ファイルが変更されました。");
            await UpdateAsync(job.Id, current => current.ContentSha256 = sha, ct);
            job.ContentSha256 = sha;
        }
        CheckMaintenanceBoundary(ct);

        Guid sessionId;
        UploadSessionDto session;
        if (Guid.TryParse(job.ServerSessionId, out sessionId))
        {
            try { session = await _protocol.GetUploadSessionAsync(sessionId, ct); }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                await InvalidateUploadSessionAsync(job.Id, ct);
                throw new IOException("アップロードセッションが期限切れまたは削除済みのため途中再開できません。再試行すると先頭から再送します。", ex);
            }
            if (!UploadSessionMatches(job, session, sessionId))
            {
                await InvalidateUploadSessionAsync(job.Id, ct);
                throw new IOException("保存されたアップロードセッションとサーバーの情報が一致しません。再試行すると新しいセッションで先頭から再送します。");
            }
        }
        else
        {
            var metadata = await _protocol.GetDownloadMetadataV2Async(job.HostId, job.ShareId, job.RemotePath, ct);
            CheckMaintenanceBoundary(ct);
            if (metadata.Exists)
            {
                if (metadata.Type != "file")
                    throw new IOException("同名フォルダがあるためファイルを転送できません。");
                if (job.ConflictPolicy == TransferConflictPolicies.Skip)
                    return TransferJobStates.Skipped;
                if (job.ConflictPolicy == TransferConflictPolicies.Ask)
                {
                    await UpdateAsync(job.Id, current =>
                    {
                        current.ConflictDestinationSize = metadata.Size;
                        current.ConflictDestinationModifiedUtc = metadata.ModifiedAtUtc;
                    }, ct);
                    throw new TransferConflictException("リモートに同名ファイルがあります。上書き・スキップ・別名を選択してください。");
                }
                if (job.ConflictPolicy == TransferConflictPolicies.Rename)
                {
                    job.RemotePath = await FindAvailableRemotePathAsync(job, ct);
                    await UpdateAsync(job.Id, current => current.RemotePath = job.RemotePath, ct);
                }
            }
            session = await CreateUploadSessionAsync(job, ct);
            sessionId = session.SessionId;
        }

        if (session.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            return TransferJobStates.Completed;
        if (!session.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            await InvalidateUploadSessionAsync(job.Id, ct);
            throw new IOException($"アップロードセッションを途中再開できません ({session.Status})。再試行すると先頭から再送します。");
        }
        if (session.UploadedOffset < 0 || session.UploadedOffset > job.TotalBytes)
            throw new InvalidDataException("サーバーの再開位置がファイル範囲外です。");
        await UpdateAsync(job.Id, current =>
        {
            current.ServerSessionId = sessionId.ToString("D");
            current.BytesTransferred = session.UploadedOffset;
        }, ct);

        await using var stream = new FileStream(
            job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = session.UploadedOffset;
        var offset = session.UploadedOffset;
        var buffer = new byte[ChunkSize];
        while (offset < job.TotalBytes)
        {
            CheckMaintenanceBoundary(ct);
            var wanted = (int)Math.Min(buffer.Length, job.TotalBytes - offset);
            var count = await ReadExactlyUpToAsync(stream, buffer, wanted, ct);
            if (count != wanted) throw new EndOfStreamException("アップロード元ファイルが途中で短くなりました。");
            var chunkSha = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))).ToLowerInvariant();
            session = await _protocol.UploadChunkAsync(sessionId, offset, buffer, count, chunkSha, ct);
            EnsureUploadSessionMatches(job, session, sessionId);
            if (session.UploadedOffset < offset + count || session.UploadedOffset > job.TotalBytes)
                throw new InvalidDataException("サーバーから不正なアップロード位置が返されました。");
            offset = session.UploadedOffset;
            stream.Position = offset;
            await UpdateAsync(job.Id, current => current.BytesTransferred = offset, ct);
        }

        CheckMaintenanceBoundary(ct);
        session = await _protocol.CompleteUploadSessionAsync(sessionId, ct);
        EnsureUploadSessionMatches(job, session, sessionId);
        if (!session.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"アップロードを確定できませんでした ({session.ErrorCode ?? session.Status})。");
        await UpdateAsync(job.Id, current =>
        {
            current.BytesTransferred = current.TotalBytes;
            current.RemoteETag = session.ETag;
        }, ct);
        return TransferJobStates.Completed;
    }

    private async Task<UploadSessionDto> CreateUploadSessionAsync(TransferJobRecord job, CancellationToken ct)
    {
        CheckMaintenanceBoundary(ct);
        var session = await _protocol.CreateUploadSessionAsync(new CreateUploadSessionRequest
        {
            HostId = job.HostId,
            ShareId = job.ShareId,
            Path = job.RemotePath,
            TotalSize = job.TotalBytes,
            Sha256 = job.ContentSha256!,
            IdempotencyKey = job.ServerSessionGeneration == 0
                ? job.Id
                : $"{job.Id}-{job.ServerSessionGeneration}",
            Overwrite = job.ConflictPolicy == TransferConflictPolicies.Overwrite,
        }, ct);
        EnsureUploadSessionMatches(job, session, session.SessionId);
        await UpdateAsync(job.Id, current =>
        {
            current.ServerSessionId = session.SessionId.ToString("D");
            current.BytesTransferred = session.UploadedOffset;
        }, ct);
        return session;
    }

    private Task InvalidateUploadSessionAsync(string jobId, CancellationToken ct)
        => UpdateAsync(jobId, current =>
        {
            current.ServerSessionId = null;
            current.ServerSessionGeneration++;
            current.BytesTransferred = 0;
        }, ct);

    private static bool UploadSessionMatches(
        TransferJobRecord job, UploadSessionDto session, Guid expectedSessionId)
        => expectedSessionId != Guid.Empty &&
           session.SessionId == expectedSessionId &&
           session.HostId == job.HostId &&
           session.ShareId == job.ShareId &&
           string.Equals(PathHelper.NormalizePath(session.Path), PathHelper.NormalizePath(job.RemotePath),
               StringComparison.OrdinalIgnoreCase) &&
           session.TotalSize == job.TotalBytes &&
           string.Equals(session.Sha256, job.ContentSha256, StringComparison.OrdinalIgnoreCase) &&
           session.Overwrite == (job.ConflictPolicy == TransferConflictPolicies.Overwrite);

    private static void EnsureUploadSessionMatches(
        TransferJobRecord job, UploadSessionDto session, Guid expectedSessionId)
    {
        if (!UploadSessionMatches(job, session, expectedSessionId))
            throw new InvalidDataException("サーバーから別のアップロードセッション情報が返されました。転送を停止しました。");
        if (session.UploadedOffset < 0 || session.UploadedOffset > job.TotalBytes)
            throw new InvalidDataException("サーバーのアップロード位置がファイル範囲外です。");
    }

    private async Task<string> ProcessDownloadAsync(TransferJobRecord job, CancellationToken ct)
    {
        var metadata = await _protocol.GetDownloadMetadataV2Async(job.HostId, job.ShareId, job.RemotePath, ct);
        CheckMaintenanceBoundary(ct);
        if (!metadata.Exists || metadata.Type != "file" || !metadata.Size.HasValue)
            throw new FileNotFoundException("ダウンロード元ファイルが見つかりません。", job.RemotePath);
        if (string.IsNullOrWhiteSpace(metadata.ETag))
            throw new InvalidDataException("サーバーからファイル世代情報 (ETag) が返されませんでした。");
        string expectedSha256;
        try
        {
            expectedSha256 = TransferV2Validation.NormalizeSha256(metadata.Sha256 ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("サーバーからファイル全体のSHA-256が返されませんでした。", ex);
        }

        var destination = job.LocalPath;
        // 最終rename後、Completed状態の保存前にプロセスが停止した場合の回復。
        // 直前に確定済みだった世代・サイズ・全体SHAがすべて一致する場合だけ再転送せず完了扱いにする。
        if (File.Exists(destination) &&
            job.BytesTransferred == metadata.Size.Value &&
            job.TotalBytes == metadata.Size.Value &&
            string.Equals(job.RemoteETag, metadata.ETag, StringComparison.Ordinal) &&
            string.Equals(job.ContentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase) &&
            new FileInfo(destination).Length == metadata.Size.Value)
        {
            var committedSha256 = await ComputeFileSha256Async(destination, ct);
            if (CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(committedSha256),
                    Convert.FromHexString(expectedSha256)))
                return TransferJobStates.Completed;
        }
        if (File.Exists(destination))
        {
            if (job.ConflictPolicy == TransferConflictPolicies.Skip)
            {
                DeletePartialBestEffort(destination, job.Id);
                return TransferJobStates.Skipped;
            }
            if (job.ConflictPolicy == TransferConflictPolicies.Ask)
            {
                var existing = new FileInfo(destination);
                await UpdateAsync(job.Id, current =>
                {
                    current.TotalBytes = metadata.Size.Value;
                    current.SourceLastWriteUtc = metadata.ModifiedAtUtc;
                    current.ConflictDestinationSize = existing.Length;
                    current.ConflictDestinationModifiedUtc = existing.LastWriteTimeUtc;
                }, ct);
                throw new TransferConflictException("ローカルに同名ファイルがあります。上書き・スキップ・別名を選択してください。");
            }
            if (job.ConflictPolicy == TransferConflictPolicies.Rename)
            {
                var oldDestination = destination;
                destination = await FindAndReserveAvailableLocalPathAsync(job.Id, destination, ct);
                job.LocalPath = destination;
                await UpdateAsync(job.Id, current => current.LocalPath = destination, ct);
                MovePartialBestEffort(oldDestination, destination, job.Id);
            }
        }
        if (Directory.Exists(destination))
        {
            if (job.ConflictPolicy == TransferConflictPolicies.Skip)
            {
                DeletePartialBestEffort(destination, job.Id);
                return TransferJobStates.Skipped;
            }
            throw new IOException("ローカルに同名フォルダがあるためファイルを保存できません。");
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException("ダウンロード先フォルダを特定できません。");
        Directory.CreateDirectory(parent);
        var tempPath = PartialPath(destination, job.Id);
        var expectedSize = metadata.Size.Value;
        var resetPartial = !string.Equals(job.RemoteETag, metadata.ETag, StringComparison.Ordinal) ||
                           !string.Equals(job.ContentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase) ||
                           (File.Exists(tempPath) && new FileInfo(tempPath).Length > expectedSize);
        if (resetPartial && File.Exists(tempPath))
        {
            using var reset = new FileStream(tempPath, FileMode.Truncate, FileAccess.Write, FileShare.None);
        }

        var actualOffset = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        await UpdateAsync(job.Id, current =>
        {
            current.TotalBytes = expectedSize;
            current.BytesTransferred = actualOffset;
            current.RemoteETag = metadata.ETag;
            current.ContentSha256 = expectedSha256;
        }, ct);

        await using (var output = new FileStream(
            tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            output.Position = actualOffset;
            while (actualOffset < expectedSize)
            {
                CheckMaintenanceBoundary(ct);
                var length = (int)Math.Min(ChunkSize, expectedSize - actualOffset);
                try
                {
                    await _protocol.DownloadRangeV2Async(
                        job.HostId, job.ShareId, job.RemotePath, actualOffset, length, metadata.ETag, output, ct);
                }
                catch
                {
                    // 応答失敗中に書かれた未検証rangeを再開位置として使わない。
                    output.SetLength(actualOffset);
                    throw;
                }
                await output.FlushAsync(ct);
                actualOffset += length;
                await UpdateAsync(job.Id, current => current.BytesTransferred = actualOffset, ct);
            }
        }

        CheckMaintenanceBoundary(ct);
        var actualSha256 = await ComputeFileSha256Async(tempPath, ct);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSha256),
                Convert.FromHexString(expectedSha256)))
        {
            DeletePartialBestEffort(destination, job.Id);
            await UpdateAsync(job.Id, current => current.BytesTransferred = 0, ct);
            throw new InvalidDataException("ダウンロードしたファイル全体のSHA-256が一致しません。部分ファイルを破棄しました。");
        }

        // 各rangeのchecksumに加え、metadata取得時の全体SHAとも一致した後だけ最終名へcommitする。
        CheckMaintenanceBoundary(ct);
        try { File.Move(tempPath, destination, overwrite: job.ConflictPolicy == TransferConflictPolicies.Overwrite); }
        catch (IOException) when (job.ConflictPolicy != TransferConflictPolicies.Overwrite && File.Exists(destination))
        {
            var existing = new FileInfo(destination);
            await UpdateAsync(job.Id, current =>
            {
                current.SourceLastWriteUtc = metadata.ModifiedAtUtc;
                current.ConflictDestinationSize = existing.Length;
                current.ConflictDestinationModifiedUtc = existing.LastWriteTimeUtc;
            }, ct);
            throw new TransferConflictException("保存直前にローカルに同名ファイルが作成されました。上書き・スキップ・別名を選択してください。");
        }
        return TransferJobStates.Completed;
    }

    private async Task<string> FindAvailableRemotePathAsync(TransferJobRecord job, CancellationToken ct)
    {
        var directory = job.RemotePath.Contains('/') ? job.RemotePath[..job.RemotePath.LastIndexOf('/')] : "/";
        if (string.IsNullOrEmpty(directory)) directory = "/";
        var name = job.RemotePath[(job.RemotePath.LastIndexOf('/') + 1)..];
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 1; i <= 1000; i++)
        {
            CheckMaintenanceBoundary(ct);
            var candidateName = $"{stem} ({i}){extension}";
            var candidate = directory == "/" ? "/" + candidateName : directory + "/" + candidateName;
            var metadata = await _protocol.GetDownloadMetadataV2Async(job.HostId, job.ShareId, candidate, ct);
            if (!metadata.Exists && await TryReserveActiveResourceAsync(
                    job.Id, UploadResourceKey(job.HostId, job.ShareId, candidate), ct))
                return candidate;
        }
        throw new IOException("別名の空きファイル名を作成できませんでした。");
    }

    private async Task<string> FindAndReserveAvailableLocalPathAsync(
        string jobId, string path, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i <= 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) &&
                await TryReserveActiveResourceAsync(jobId, DownloadResourceKey(candidate), ct))
                return candidate;
        }
        throw new IOException("別名の空きファイル名を作成できませんでした。");
    }

    private async Task<bool> TryReserveActiveResourceAsync(
        string jobId, string resourceKey, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            if (!_activeResourceKeys.TryGetValue(jobId, out var ownKeys)) return false;
            if (_activeResourceKeys.Any(pair =>
                    !string.Equals(pair.Key, jobId, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value.Contains(resourceKey)))
                return false;
            if (_jobs.Any(job =>
                    !IdEquals(job, jobId) &&
                    job.State == TransferJobStates.Canceling &&
                    string.Equals(ResourceKey(job), resourceKey, StringComparison.OrdinalIgnoreCase)))
                return false;
            ownKeys.Add(resourceKey);
            return true;
        }
        finally { _mutex.Release(); }
    }

    private static string PartialPath(string destination, string jobId)
    {
        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException("ダウンロード先フォルダを特定できません。");
        return Path.Combine(parent, $".{Path.GetFileName(destination)}.{jobId}.watashi-part");
    }

    private static void MovePartialBestEffort(string oldDestination, string newDestination, string jobId)
    {
        try
        {
            var oldPartial = PartialPath(oldDestination, jobId);
            var newPartial = PartialPath(newDestination, jobId);
            if (File.Exists(oldPartial) && !File.Exists(newPartial)) File.Move(oldPartial, newPartial);
        }
        catch { /* 移動できなければ旧partialを残し、新しい転送を安全に開始する。 */ }
    }

    private static void DeletePartialBestEffort(string destination, string jobId)
    {
        try
        {
            var path = PartialPath(destination, jobId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static async Task<int> ReadExactlyUpToAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private async Task<TransferJobRecord?> FindSnapshotAsync(string id, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            var job = _jobs.FirstOrDefault(x => IdEquals(x, id));
            return job is null ? null : Clone(job);
        }
        finally { _mutex.Release(); }
    }

    private Task UpdateAsync(string id, Action<TransferJobRecord> update, CancellationToken ct)
        => MutateAsync(job => IdEquals(job, id), update, signal: false, ct);

    private async Task SetTerminalAsync(string id, string state, string? error, CancellationToken ct)
    {
        // An error/confirmation discovered just as the user cancels must not replace
        // Canceling. A confirmed completion still wins over cancellation.
        await MutateAsync(job => IdEquals(job, id) &&
            !(job.State == TransferJobStates.Canceling &&
              state is TransferJobStates.Failed or TransferJobStates.ConflictWaiting), job =>
        {
            job.State = state;
            job.LastError = error;
            job.MaintenanceResumeState = null;
            job.NextAttemptAtUtc = null;
            if (state is TransferJobStates.Completed or TransferJobStates.Skipped)
            {
                job.BytesTransferred = job.TotalBytes;
                job.CompletedAtUtc = _utcNow();
            }
        }, signal: false, ct);
        await CleanupExpiredHistoryBestEffortAsync(ct);
    }

    private async Task PersistTerminalSafelyAsync(string id, string state, string? error)
    {
        try
        {
            await SetTerminalAsync(id, state, error, CancellationToken.None);
        }
        catch (Exception ex)
        {
            QueuePublication publication;
            await _mutex.WaitAsync();
            try
            {
                var job = _jobs.FirstOrDefault(x => IdEquals(x, id));
                if (job is not null)
                {
                    job.State = TransferJobStates.Paused;
                    job.LastError = "転送状態を保存できなかったため一時停止しました: " + ex.Message;
                    job.UpdatedAt = DateTime.UtcNow;
                }
                publication = CapturePublicationLocked();
            }
            finally { _mutex.Release(); }
            Publish(publication);
            PublishError("転送キューの状態を保存できませんでした。保存先を確認してください: " + ex.Message);
        }
    }

    private async Task MutateAsync(
        Func<TransferJobRecord, bool> predicate,
        Action<TransferJobRecord> mutation,
        bool signal,
        CancellationToken ct)
    {
        QueuePublication? publication = null;
        await _mutex.WaitAsync(ct);
        try
        {
            var before = CloneAllLocked();
            var changed = false;
            foreach (var job in _jobs.Where(predicate))
            {
                mutation(job);
                job.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
            if (!changed) return;
            ApplyMaintenanceLocked();
            await SaveOrRollbackLockedAsync(before, ct);
            publication = CapturePublicationLocked();
        }
        finally { _mutex.Release(); }
        if (publication is not null) Publish(publication);
        if (signal) SignalPump();
    }

    private async Task RemoveAsync(Func<TransferJobRecord, bool> predicate, CancellationToken ct)
    {
        QueuePublication? publication = null;
        await _mutex.WaitAsync(ct);
        try
        {
            var before = CloneAllLocked();
            if (_jobs.RemoveAll(job => predicate(job)) == 0) return;
            await SaveOrRollbackLockedAsync(before, ct);
            publication = CapturePublicationLocked();
        }
        finally { _mutex.Release(); }
        if (publication is not null) Publish(publication);
    }

    private IReadOnlyList<TransferJobRecord> SnapshotLocked() => _jobs.Select(Clone).ToArray();

    private QueuePublication CapturePublicationLocked()
        => new(++_nextPublicationRevision, SnapshotLocked());

    private List<TransferJobRecord> CloneAllLocked() => _jobs.Select(Clone).ToList();

    private async Task SaveOrRollbackLockedAsync(List<TransferJobRecord> before, CancellationToken ct)
    {
        try
        {
            await _store.SaveAsync(_jobs, ct);
        }
        catch
        {
            _jobs.Clear();
            _jobs.AddRange(before);
            throw;
        }
    }

    private static TransferJobRecord Clone(TransferJobRecord source) => new()
    {
        Id = source.Id,
        Direction = source.Direction,
        LocalPath = source.LocalPath,
        HostId = source.HostId,
        ShareId = source.ShareId,
        RemotePath = source.RemotePath,
        TotalBytes = source.TotalBytes,
        BytesTransferred = source.BytesTransferred,
        State = source.State,
        ConflictPolicy = source.ConflictPolicy,
        AttemptCount = source.AttemptCount,
        LastError = source.LastError,
        MaintenanceResumeState = source.MaintenanceResumeState,
        ConflictDestinationSize = source.ConflictDestinationSize,
        ConflictDestinationModifiedUtc = source.ConflictDestinationModifiedUtc,
        ServerSessionId = source.ServerSessionId,
        ServerSessionGeneration = source.ServerSessionGeneration,
        ContentSha256 = source.ContentSha256,
        RemoteETag = source.RemoteETag,
        SourceLastWriteUtc = source.SourceLastWriteUtc,
        NextAttemptAtUtc = source.NextAttemptAtUtc,
        CompletedAtUtc = source.CompletedAtUtc,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
    };

    private static TransferJobRecord NewJob(
        string direction, string localPath, int hostId, int shareId, string remotePath, string conflictPolicy)
    {
        if (hostId <= 0) throw new ArgumentOutOfRangeException(nameof(hostId));
        if (shareId <= 0) throw new ArgumentOutOfRangeException(nameof(shareId));
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith('/'))
            throw new ArgumentException("リモートパスは / から始めてください。", nameof(remotePath));
        if (conflictPolicy is not (TransferConflictPolicies.Ask or TransferConflictPolicies.Overwrite or
            TransferConflictPolicies.Skip or TransferConflictPolicies.Rename))
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
        return new TransferJobRecord
        {
            Direction = direction,
            LocalPath = localPath,
            HostId = hostId,
            ShareId = shareId,
            RemotePath = remotePath,
            ConflictPolicy = conflictPolicy,
        };
    }

    private static bool IdEquals(TransferJobRecord job, string id)
        => string.Equals(job.Id, id, StringComparison.OrdinalIgnoreCase);

    private static string ResourceKey(TransferJobRecord job)
        => job.Direction == TransferDirections.Upload
            ? UploadResourceKey(job.HostId, job.ShareId, job.RemotePath)
            : DownloadResourceKey(job.LocalPath);

    private static string UploadResourceKey(int hostId, int shareId, string path)
        => $"upload:{hostId}:{shareId}:{PathHelper.NormalizePath(path)}";

    private static string DownloadResourceKey(string path)
        => "download:" + Path.GetFullPath(path);

    private static bool IsTerminal(string state) => state is
        TransferJobStates.Completed or TransferJobStates.Failed or TransferJobStates.Canceled or TransferJobStates.Skipped;

    private static string FriendlyError(Exception ex) => ex switch
    {
        TransferConflictException => ex.Message,
        ApiException api when api.StatusCode == HttpStatusCode.PreconditionFailed =>
            "転送中に元ファイルが変更されました。再試行してください。",
        ApiException api when api.StatusCode == HttpStatusCode.Forbidden =>
            "転送権限がありません。権限が変更された可能性があります。",
        ApiException api when api.StatusCode == HttpStatusCode.Unauthorized =>
            "認証の有効期限が切れました。再ログイン後に再試行してください。",
        ApiException api => api.Message,
        IOException => ex.Message,
        _ => "転送に失敗しました: " + ex.Message,
    };

    private async Task<bool> ScheduleAutomaticRetrySafelyAsync(string jobId, Exception error)
    {
        if (_lifetime.IsCancellationRequested || !IsTransient(error)) return false;
        QueuePublication? publication = null;
        DateTime? retryAt = null;
        try
        {
            await _mutex.WaitAsync();
            try
            {
                var job = _jobs.FirstOrDefault(x => IdEquals(x, jobId));
                if (job is null || job.State != TransferJobStates.Running || job.AttemptCount >= 5)
                    return false;
                var before = CloneAllLocked();
                var delay = _retryDelayFactory(job.AttemptCount);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                retryAt = DateTime.UtcNow.Add(delay);
                job.State = TransferJobStates.RetryWaiting;
                job.NextAttemptAtUtc = retryAt;
                job.LastError = $"一時的な通信エラーのため自動再試行を待っています: {FriendlyError(error)}";
                job.UpdatedAt = DateTime.UtcNow;
                ApplyMaintenanceLocked();
                await SaveOrRollbackLockedAsync(before, CancellationToken.None);
                publication = CapturePublicationLocked();
            }
            finally { _mutex.Release(); }
        }
        catch (Exception ex)
        {
            PublishError("自動再試行の状態を保存できませんでした: " + ex.Message);
            return false;
        }
        if (publication is not null) Publish(publication);
        ScheduleRetryWake(retryAt);
        return true;
    }

    private void ScheduleRetryWake(DateTime? retryAtUtc)
    {
        if (!retryAtUtc.HasValue || retryAtUtc <= DateTime.UtcNow)
        {
            SignalPump();
            return;
        }
        _ = WakePumpAtAsync(retryAtUtc.Value);
    }

    private async Task WakePumpAtAsync(DateTime retryAtUtc)
    {
        try
        {
            var delay = retryAtUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _lifetime.Token);
            // 状態変更はpumpのclaimと同じatomic saveで行う。timer側でRetryWaiting→Queuedを
            // 保存すると、一時的なディスク障害だけでwake自体を失う。active workerの終了と
            // 同時刻になった場合も拾えるよう、due状態が残る間だけ短く再通知する。
            for (var attempt = 0; attempt < 10; attempt++)
            {
                SignalPump();
                await Task.Delay(TimeSpan.FromMilliseconds(100), _lifetime.Token);
                await _mutex.WaitAsync(_lifetime.Token);
                try
                {
                    var stillDue = _jobs.Any(job =>
                        job.State == TransferJobStates.RetryWaiting &&
                        (!job.NextAttemptAtUtc.HasValue || job.NextAttemptAtUtc <= DateTime.UtcNow) &&
                        !_active.ContainsKey(job.Id));
                    if (!stillDue) return;
                }
                finally { _mutex.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PublishError("自動再試行タイマーでエラーが発生しました。再試行します: " + ex.Message);
            if (!_lifetime.IsCancellationRequested)
                _ = WakePumpAtAsync(DateTime.UtcNow.AddSeconds(2));
        }
    }

    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or TimeoutException ||
           ex is OperationCanceledException ||
           ex is ApiException api && api.StatusCode is
               HttpStatusCode.RequestTimeout or
               HttpStatusCode.TooManyRequests or
               HttpStatusCode.InternalServerError or
               HttpStatusCode.BadGateway or
               HttpStatusCode.ServiceUnavailable or
               HttpStatusCode.GatewayTimeout;

    private static TimeSpan DefaultRetryDelay(int attemptCount)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Clamp(attemptCount, 1, 6)));
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
    }

    private void SignalPump()
    {
        if (_disposed) return;
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task<RemoteCancelResult> CancelRemoteSessionBestEffortAsync(string jobId)
    {
        TransferJobRecord? job = null;
        await _mutex.WaitAsync();
        try
        {
            var current = _jobs.FirstOrDefault(x => IdEquals(x, jobId));
            if (current is not null) job = Clone(current);
        }
        finally { _mutex.Release(); }
        if (job is null || !Guid.TryParse(job.ServerSessionId, out var sessionId))
            return RemoteCancelResult.Canceled;
        UploadSessionDto response;
        try
        {
            response = await CancelUploadSessionBoundedAsync(sessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            PublishError("サーバー側のアップロード一時データを中止できませんでした。期限切れ後に自動回収されます: " + ex.Message);
            return RemoteCancelResult.Uncertain;
        }
        if (!UploadSessionMatches(job, response, sessionId))
        {
            PublishError("サーバーから別のアップロードセッション情報が返されたため、中止結果を採用しませんでした。");
            return RemoteCancelResult.Uncertain;
        }
        if (response.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await UpdateAsync(jobId, job =>
                {
                    job.BytesTransferred = job.TotalBytes;
                    job.RemoteETag = response.ETag;
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                PublishError("完了済み転送の状態を保存できませんでした: " + ex.Message);
            }
            return RemoteCancelResult.Completed;
        }
        if (!IsCanceledUploadSession(response.Status))
        {
            PublishError($"サーバー側のアップロード中止結果を確認できませんでした ({response.Status})。");
            return RemoteCancelResult.Uncertain;
        }
        try
        {
            await UpdateAsync(jobId, job =>
            {
                job.ServerSessionId = null;
                job.ServerSessionGeneration++;
                job.BytesTransferred = 0;
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            PublishError("中止済み転送の状態を保存できませんでした: " + ex.Message);
        }
        return RemoteCancelResult.Canceled;
    }

    private async ValueTask<BatchCancelOutcome> CancelInactiveSessionAsync(
        TransferJobRecord job, CancellationToken ct)
    {
        if (!Guid.TryParse(job.ServerSessionId, out var sessionId))
            return new BatchCancelOutcome(job.Id, RemoteCancelResult.Canceled, null, null);
        try
        {
            var response = await CancelUploadSessionBoundedAsync(sessionId, ct);
            if (!UploadSessionMatches(job, response, sessionId))
                return new BatchCancelOutcome(
                    job.Id, RemoteCancelResult.Uncertain, null,
                    "別のアップロードセッション応答を検出したため中止結果を採用しませんでした。");
            if (response.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                return new BatchCancelOutcome(job.Id, RemoteCancelResult.Completed, response.ETag, null);
            if (IsCanceledUploadSession(response.Status))
                return new BatchCancelOutcome(job.Id, RemoteCancelResult.Canceled, null, null);
            return new BatchCancelOutcome(
                job.Id, RemoteCancelResult.Uncertain, null,
                $"サーバー側の中止結果を確認できませんでした ({response.Status})。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new BatchCancelOutcome(
                job.Id, RemoteCancelResult.Uncertain, null,
                "サーバー側の中止結果を確認できませんでした: " + ex.Message);
        }
    }

    private async Task<bool> ReconcileCanceledRemoteSessionAsync(
        string jobId, Guid sessionId, CancellationToken ct)
    {
        var job = await FindSnapshotAsync(jobId, ct);
        if (job is null) return false;
        UploadSessionDto response;
        try
        {
            response = await CancelUploadSessionBoundedAsync(sessionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            PublishError("サーバー側のアップロード一時データを中止できませんでした。期限切れ後に自動回収されます: " + ex.Message);
            await SetTerminalAsync(
                jobId, TransferJobStates.Paused,
                "サーバー側の中止結果を確認できませんでした。再試行または再度中止してください。", ct);
            return true;
        }
        if (!UploadSessionMatches(job, response, sessionId))
        {
            await SetTerminalAsync(
                jobId, TransferJobStates.Paused,
                "別のアップロードセッション応答を検出したため中止結果を採用しませんでした。", ct);
            return true;
        }
        if (response.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            await SetTerminalAsync(jobId, TransferJobStates.Completed, null, ct);
            await UpdateAsync(jobId, job => job.RemoteETag = response.ETag, ct);
            return true;
        }
        if (!IsCanceledUploadSession(response.Status))
        {
            await SetTerminalAsync(
                jobId, TransferJobStates.Paused,
                $"サーバー側の中止結果を確認できませんでした ({response.Status})。", ct);
            return true;
        }
        await UpdateAsync(jobId, job =>
        {
            job.ServerSessionId = null;
            job.ServerSessionGeneration++;
            job.BytesTransferred = 0;
        }, ct);
        return false;
    }

    private async Task<UploadSessionDto> CancelUploadSessionBoundedAsync(
        Guid sessionId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RemoteCancelTimeout);
        try
        {
            return await _protocol.CancelUploadSessionAsync(sessionId, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"アップロード中止の確認が{RemoteCancelTimeout.TotalSeconds:0}秒でタイムアウトしました。");
        }
    }

    private async ValueTask CleanupRemovedUploadSessionBestEffortAsync(
        TransferJobRecord job, CancellationToken ct)
    {
        if (!Guid.TryParse(job.ServerSessionId, out var sessionId)) return;
        try
        {
            var response = await CancelUploadSessionBoundedAsync(sessionId, ct);
            if (!UploadSessionMatches(job, response, sessionId))
                PublishError($"{Path.GetFileName(job.LocalPath)} の一時転送について別session応答を検出しました。期限切れ後に回収されます。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            PublishError($"{Path.GetFileName(job.LocalPath)} の一時転送を削除できませんでした。期限切れ後に回収されます: {ex.Message}");
        }
    }

    private static bool IsCanceledUploadSession(string status)
        => status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("canceled", StringComparison.OrdinalIgnoreCase) ||
           status.Equals("expired", StringComparison.OrdinalIgnoreCase);

    private enum RemoteCancelResult
    {
        Canceled,
        Completed,
        Uncertain,
    }

    private sealed record BatchCancelOutcome(
        string JobId,
        RemoteCancelResult Result,
        string? ETag,
        string? Error);

    private void Publish(QueuePublication publication)
    {
        lock (_publicationLock)
        {
            // 状態更新と通知の間でthreadが入れ替わっても、古いsnapshotを後から表示しない。
            if (publication.Revision <= _lastPublishedRevision) return;
            _lastPublishedRevision = publication.Revision;
            var handlers = QueueChanged;
            if (handlers is null) return;
            foreach (Action<IReadOnlyList<TransferJobRecord>> handler in handlers.GetInvocationList())
            {
                try { handler(publication.Jobs); }
                catch { /* 表示側の例外で永続転送ワーカーを停止させない。 */ }
            }
        }
    }

    private sealed record QueuePublication(long Revision, IReadOnlyList<TransferJobRecord> Jobs);

    private void PublishError(string message)
    {
        var handlers = QueueError;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(message); }
            catch { }
        }
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!_initialized) throw new InvalidOperationException("転送キューが初期化されていません。");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _disposing, this);

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await _mutex.WaitAsync();
            try
            {
                if (_disposed || _disposing) return;
                _disposing = true;
            }
            finally { _mutex.Release(); }

            _lifetime.Cancel();
            try { _signal.Release(); } catch { }
            if (_pumpTask is not null)
            {
                try { await _pumpTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PublishError("転送キュー停止時にエラーが発生しました: " + ex.Message); }
            }
            Task[] workers;
            lock (_workerTasks) workers = _workerTasks.ToArray();
            try { await Task.WhenAll(workers); } catch { }
            _exclusiveLease?.Dispose();
            _exclusiveLease = null;
            _lifetime.Dispose();

            await _mutex.WaitAsync();
            try
            {
                _disposed = true;
                _disposing = false;
            }
            finally { _mutex.Release(); }
        }
        finally { _lifecycleGate.Release(); }
    }

    private sealed class TransferConflictException(string message) : IOException(message);
    private sealed class MaintenanceHoldException : Exception { }
}
