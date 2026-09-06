using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Helpers;

namespace Watashi.Client.ViewModels;

public sealed class TransferQueueItemViewModel : ObservableObject
{
    public TransferJobRecord Job { get; private set; }
    public string Id => Job.Id;
    public string FileName => Job.Direction == TransferDirections.Upload
        ? Path.GetFileName(Job.LocalPath)
        : Path.GetFileName(Job.RemotePath.TrimEnd('/'));
    public string DirectionLabel => Job.Direction == TransferDirections.Upload ? "アップロード" : "ダウンロード";
    public string StateLabel => Job.State switch
    {
        TransferJobStates.Queued => "待機中",
        TransferJobStates.Running => "転送中",
        TransferJobStates.Canceling => "中止処理中",
        TransferJobStates.RetryWaiting => "再試行待ち",
        TransferJobStates.Paused => "一時停止",
        TransferJobStates.MaintenanceWaiting => "メンテナンス待ち",
        TransferJobStates.ConflictWaiting => "競合の確認待ち",
        TransferJobStates.Completed => "完了",
        TransferJobStates.Failed => "失敗",
        TransferJobStates.Canceled => "中止",
        TransferJobStates.Skipped => "スキップ",
        _ => Job.State,
    };
    public string ConflictLabel => Job.ConflictPolicy switch
    {
        TransferConflictPolicies.Ask => "競合時に確認",
        TransferConflictPolicies.Overwrite => "上書き",
        TransferConflictPolicies.Skip => "スキップ",
        TransferConflictPolicies.Rename => "別名",
        _ => Job.ConflictPolicy,
    };
    public int Percent => Job.TotalBytes > 0
        ? (int)Math.Clamp(Job.BytesTransferred * 100 / Job.TotalBytes, 0, 100)
        : Job.State is TransferJobStates.Completed or TransferJobStates.Skipped ? 100 : 0;
    public string DirectionArrow => Job.Direction == TransferDirections.Upload ? "↑" : "↓";
    public string CompactProgress => Job.State == TransferJobStates.Canceling ? "中止中" : $"{Percent}%";
    public string ProgressDescription => $"{DirectionLabel}: {FileName} / {StateLabel} / {ProgressText}\n転送元: {SourceText}\n転送先: {DestinationText}";
    public string ProgressText => $"{FormatBytes(Job.BytesTransferred)} / {FormatBytes(Job.TotalBytes)} ({Percent}%)";
    public string SourceText => Job.Direction == TransferDirections.Upload ? Job.LocalPath : Job.RemotePath;
    public string DestinationText => Job.Direction == TransferDirections.Upload ? Job.RemotePath : Job.LocalPath;
    public string? ErrorText => Job.LastError;
    public string NextActionHint => Job.State switch
    {
        TransferJobStates.Failed => "エラー内容を確認して「再試行」を選んでください。",
        TransferJobStates.Paused or TransferJobStates.Canceled => "続ける場合は「再開」を選んでください。",
        TransferJobStates.ConflictWaiting => "転送元・先の情報を比較し、上書き・スキップ・別名から選んでください。",
        TransferJobStates.MaintenanceWaiting => "メンテナンス終了と更新の確認を待っています。",
        TransferJobStates.RetryWaiting => "予定時刻になると自動で再試行します。",
        _ => string.Empty,
    };
    public string SourceMetadata => $"{FormatBytes(Job.TotalBytes)} / 更新: {FormatDate(Job.SourceLastWriteUtc)}";
    public string DestinationMetadata => Job.ConflictDestinationSize.HasValue
        ? $"{FormatBytes(Job.ConflictDestinationSize.Value)} / 更新: {FormatDate(Job.ConflictDestinationModifiedUtc)}"
        : "競合比較の情報はありません";
    public string RetryAtText => Job.State == TransferJobStates.RetryWaiting && Job.NextAttemptAtUtc.HasValue
        ? $"再試行予定: {FormatDate(Job.NextAttemptAtUtc)}（この端末の時刻）" : string.Empty;
    public bool IsConflict => Job.State == TransferJobStates.ConflictWaiting;
    public bool NeedsAttention => Job.State is TransferJobStates.ConflictWaiting or TransferJobStates.Failed or TransferJobStates.Paused;
    public bool IsFinished => Job.State is TransferJobStates.Completed or TransferJobStates.Skipped or TransferJobStates.Canceled;
    public bool IsRunning => Job.State is TransferJobStates.Running or TransferJobStates.Canceling;
    public bool CanCancel => Job.State is TransferJobStates.Queued or TransferJobStates.Running or
        TransferJobStates.RetryWaiting or TransferJobStates.Paused or
        TransferJobStates.MaintenanceWaiting or TransferJobStates.ConflictWaiting;
    public bool CanRetry => Job.State == TransferJobStates.Failed;
    public bool CanResume => Job.State is TransferJobStates.Paused or TransferJobStates.Canceled;

    public TransferQueueItemViewModel(TransferJobRecord job) => Job = job;

    public void Update(TransferJobRecord job)
    {
        Job = job;
        OnPropertyChanged(string.Empty);
    }

    private static string FormatDate(DateTime? value)
        => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : "不明";

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}

public sealed partial class TransferFilterOption(string key) : ObservableObject
{
    public string Key { get; } = key;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int count;
    public string Label => $"{Key} ({Count})";
}

public partial class TransferQueueViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TransferQueueService _queue;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;
    private readonly Dictionary<string, string> _knownStates = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<TransferQueueItemViewModel> Jobs { get; } = new();
    public ObservableCollection<TransferQueueItemViewModel> ActiveJobs { get; } = new();
    public bool HasActiveJobs => ActiveJobs.Count > 0;
    public string CompactDescription => string.Join("\n", ActiveJobs.Select(job => job.ProgressDescription).Append(CompactSummary));
    public string CompactSummary
    {
        get
        {
            var waiting = QueuedCount + RetryWaitingCount + MaintenanceWaitingCount;
            var attention = FailedCount + PausedCount + ConflictCount;
            var parts = new List<string>();
            if (waiting > 0) parts.Add($"待機 {waiting}件");
            if (attention > 0) parts.Add($"要対応 {attention}件");
            return parts.Count > 0 ? string.Join(" · ", parts) : HasActiveJobs ? "転送中" : "転送なし";
        }
    }
    public ICollectionView VisibleJobs { get; }
    public IReadOnlyList<TransferFilterOption> FilterOptions { get; } = new[] { "すべて", "実行中・待機", "要対応", "完了・中止" }.Select(key => new TransferFilterOption(key)).ToArray();
    public event Action<string>? JobCompleted;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedJob))]
    private TransferQueueItemViewModel? selectedJob;
    public bool HasSelectedJob => SelectedJob is not null;
    [ObservableProperty] private string errorMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string selectedFilter = "すべて";
    public bool IsMaintenanceBlocked => _queue.IsMaintenanceBlocked;

    public bool HasJobs => Jobs.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasFailed => Jobs.Any(x => x.Job.State == TransferJobStates.Failed);
    public bool HasPending => Jobs.Any(x => x.CanCancel);
    public int ActiveCount => Jobs.Count(x => x.Job.State is TransferJobStates.Running or TransferJobStates.Canceling);
    public int QueuedCount => Jobs.Count(x => x.Job.State == TransferJobStates.Queued);
    public int FailedCount => Jobs.Count(x => x.Job.State == TransferJobStates.Failed);
    public int RetryWaitingCount => Jobs.Count(x => x.Job.State == TransferJobStates.RetryWaiting);
    public int MaintenanceWaitingCount => Jobs.Count(x => x.Job.State == TransferJobStates.MaintenanceWaiting);
    public int PausedCount => Jobs.Count(x => x.Job.State == TransferJobStates.Paused);
    public int ConflictCount => Jobs.Count(x => x.IsConflict);
    public int CompletedCount => Jobs.Count(x => x.IsFinished);
    public string Summary => HasJobs
        ? $"転送中 {ActiveCount} / 待機 {QueuedCount} / 再試行待ち {RetryWaitingCount} / メンテ待ち {MaintenanceWaitingCount} / 確認待ち {ConflictCount} / 一時停止 {PausedCount} / 失敗 {FailedCount} / 完了・中止 {CompletedCount}"
        : "転送ジョブはありません";

    public IAsyncRelayCommand CancelSelectedCommand { get; }
    public IAsyncRelayCommand CancelAllCommand { get; }
    public IAsyncRelayCommand RetrySelectedCommand { get; }
    public IAsyncRelayCommand RetryFailedCommand { get; }
    public IAsyncRelayCommand ResumeSelectedCommand { get; }
    public IAsyncRelayCommand RetryOverwriteCommand { get; }
    public IAsyncRelayCommand RetrySkipCommand { get; }
    public IAsyncRelayCommand RetryRenameCommand { get; }
    public IAsyncRelayCommand RemoveFinishedCommand { get; }
    public IAsyncRelayCommand DiscardInterruptedCommand { get; }

    public TransferQueueViewModel(TransferQueueService queue)
    {
        _queue = queue;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        VisibleJobs = CollectionViewSource.GetDefaultView(Jobs);
        VisibleJobs.Filter = item => item is TransferQueueItemViewModel job && (SelectedFilter switch
        {
            "実行中・待機" => !job.IsFinished && !job.NeedsAttention,
            "要対応" => job.NeedsAttention,
            "完了・中止" => job.IsFinished,
            _ => true,
        });
        _queue.QueueChanged += OnQueueChanged;
        _queue.QueueError += OnQueueError;
        CancelSelectedCommand = new AsyncRelayCommand(CancelSelectedAsync, () => !IsBusy && SelectedJob?.CanCancel == true);
        CancelAllCommand = new AsyncRelayCommand(CancelAllAsync, () => !IsBusy && HasPending);
        RetrySelectedCommand = new AsyncRelayCommand(RetrySelectedAsync, () => CanStart && SelectedJob?.CanRetry == true);
        RetryFailedCommand = new AsyncRelayCommand(() => RunAsync(_queue.RetryFailedAsync), () => CanStart && HasFailed);
        ResumeSelectedCommand = new AsyncRelayCommand(ResumeSelectedAsync, () => CanStart && SelectedJob?.CanResume == true);
        RetryOverwriteCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(TransferConflictPolicies.Overwrite), () => CanStart && SelectedJob?.IsConflict == true);
        RetrySkipCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(TransferConflictPolicies.Skip), () => CanStart && SelectedJob?.IsConflict == true);
        RetryRenameCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(TransferConflictPolicies.Rename), () => CanStart && SelectedJob?.IsConflict == true);
        RemoveFinishedCommand = new AsyncRelayCommand(
            RemoveFinishedAsync, () => !IsBusy && Jobs.Any(x =>
                x.Job.State is TransferJobStates.Completed or TransferJobStates.Skipped));
        DiscardInterruptedCommand = new AsyncRelayCommand(DiscardInterruptedAsync,
            () => !IsBusy && Jobs.Any(x => x.Job.State is TransferJobStates.Failed or TransferJobStates.Canceled));
    }

    private bool CanStart => !IsBusy && !IsMaintenanceBlocked;

    public Task SetMaintenanceAsync(bool blocked, CancellationToken ct = default)
        => _queue.SetMaintenanceAsync(blocked, ct);

    private Task CancelAllAsync()
    {
        var ids = Jobs.Where(job => job.CanCancel).Select(job => job.Id).ToArray();
        if (!Confirm($"表示の絞り込みに関係なく、対象の {ids.Length} 件を中止します。続行しますか？", "転送の中止"))
            return Task.CompletedTask;
        return RunAsync(ct => _queue.CancelJobsAsync(ids, ct));
    }

    private Task RemoveFinishedAsync()
    {
        var ids = Jobs.Where(job => job.Job.State is TransferJobStates.Completed or TransferJobStates.Skipped).Select(job => job.Id).ToArray();
        if (!Confirm($"完了・スキップ済みの履歴 {ids.Length} 件を消去します。失敗・中止した転送の再開情報は残ります。", "転送履歴の整理"))
            return Task.CompletedTask;
        return RunAsync(ct => _queue.RemoveCompletedJobsAsync(ids, ct));
    }

    private Task DiscardInterruptedAsync()
    {
        var ids = Jobs.Where(job => job.Job.State is TransferJobStates.Failed or TransferJobStates.Canceled)
            .Select(job => job.Id).ToArray();
        if (!Confirm($"表示の絞り込みに関係なく、失敗・中止した {ids.Length} 件の履歴と途中データを破棄します。これらの転送は再開できなくなります。続行しますか？", "再開情報の破棄"))
            return Task.CompletedTask;
        return RunAsync(ct => _queue.DiscardInterruptedAsync(ids, ct));
    }

    private static bool Confirm(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            try
            {
                IsBusy = true;
                ErrorMessage = string.Empty;
                await _queue.InitializeAsync(ct);
                ApplySnapshot(await _queue.SnapshotAsync(ct));
                _initialized = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 再試行できるよう、失敗時は_initializedを立てない。
                ErrorMessage = ex.Message;
            }
            finally { IsBusy = false; }
        }
        finally { _initializeGate.Release(); }
    }

    public async Task<string> EnqueueUploadAsync(
        string localPath, int hostId, int shareId, string remotePath, string conflictPolicy,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _queue.EnqueueUploadAsync(localPath, hostId, shareId, remotePath, conflictPolicy, ct);
    }

    public async Task<IReadOnlyList<string>> EnqueueUploadsAsync(
        IEnumerable<UploadQueueRequest> requests,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _queue.EnqueueUploadsAsync(requests, ct);
    }

    public async Task<string> EnqueueDownloadAsync(
        int hostId, int shareId, string remotePath, string localPath, long expectedSize,
        string conflictPolicy, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _queue.EnqueueDownloadAsync(
            hostId, shareId, remotePath, localPath, expectedSize, conflictPolicy, ct);
    }

    public async Task<IReadOnlyList<string>> EnqueueDownloadsAsync(
        IEnumerable<DownloadQueueRequest> requests,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await _queue.EnqueueDownloadsAsync(requests, ct);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized) await InitializeAsync(ct);
        if (!_initialized)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(ErrorMessage)
                    ? "転送キューを初期化できませんでした。"
                    : ErrorMessage);
    }

    partial void OnSelectedJobChanged(TransferQueueItemViewModel? value) => NotifyCommands();
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedFilterChanged(string value)
    {
        VisibleJobs.Refresh();
        if (SelectedJob is not null && !VisibleJobs.Contains(SelectedJob)) SelectedJob = null;
    }

    private Task CancelSelectedAsync() => SelectedJob is null
        ? Task.CompletedTask
        : RunAsync(ct => _queue.CancelAsync(SelectedJob.Id, ct));

    private Task RetrySelectedAsync() => SelectedJob is null
        ? Task.CompletedTask
        : RunAsync(ct => _queue.RetryAsync(SelectedJob.Id, ct));

    private Task ResumeSelectedAsync() => SelectedJob is null
        ? Task.CompletedTask
        : RunAsync(ct => _queue.ResumeAsync(SelectedJob.Id, ct));

    private Task ResolveSelectedAsync(string policy) => SelectedJob is null
        ? Task.CompletedTask
        : RunAsync(ct => _queue.ResolveConflictAndRetryAsync(SelectedJob.Id, policy, ct));

    private void OnQueueChanged(IReadOnlyList<TransferJobRecord> snapshot)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) ApplySnapshot(snapshot);
        else _ = _dispatcher.InvokeAsync(() => ApplySnapshot(snapshot), DispatcherPriority.Background);
    }

    private void OnQueueError(string message)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) ErrorMessage = message;
        else _ = _dispatcher.InvokeAsync(() => ErrorMessage = message, DispatcherPriority.Background);
    }

    private void ApplySnapshot(IReadOnlyList<TransferJobRecord> snapshot)
    {
        if (_disposed) return;
        var selectedId = SelectedJob?.Id;
        var completedDirections = snapshot
            .Where(job => job.State == TransferJobStates.Completed &&
                          _knownStates.TryGetValue(job.Id, out var previous) &&
                          previous != TransferJobStates.Completed)
            .Select(job => job.Direction)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existing = Jobs.ToDictionary(job => job.Id, StringComparer.OrdinalIgnoreCase);
        var ids = snapshot.Select(job => job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statesChanged = snapshot.Any(job => !_knownStates.TryGetValue(job.Id, out var state) || state != job.State);
        for (var i = Jobs.Count - 1; i >= 0; i--)
            if (!ids.Contains(Jobs[i].Id)) Jobs.RemoveAt(i);
        foreach (var job in snapshot.OrderByDescending(x => x.CreatedAt))
        {
            if (existing.TryGetValue(job.Id, out var item)) item.Update(job);
            else
            {
                var index = 0;
                while (index < Jobs.Count && Jobs[index].Job.CreatedAt >= job.CreatedAt) index++;
                Jobs.Insert(index, new TransferQueueItemViewModel(job));
            }
        }
        if (statesChanged && SelectedFilter != "すべて") VisibleJobs.Refresh();
        _knownStates.Clear();
        foreach (var job in snapshot) _knownStates[job.Id] = job.State;
        SelectedJob = selectedId is null ? null : Jobs.FirstOrDefault(x => x.Id == selectedId && VisibleJobs.Contains(x));
        foreach (var option in FilterOptions)
            option.Count = Jobs.Count(job => option.Key switch
            {
                "実行中・待機" => !job.IsFinished && !job.NeedsAttention,
                "要対応" => job.NeedsAttention,
                "完了・中止" => job.IsFinished,
                _ => true,
            });
        var active = Jobs.Where(job => job.IsRunning).ToArray();
        for (var i = ActiveJobs.Count - 1; i >= 0; i--)
            if (!active.Contains(ActiveJobs[i])) ActiveJobs.RemoveAt(i);
        foreach (var job in active)
            if (!ActiveJobs.Contains(job)) ActiveJobs.Add(job);
        OnPropertyChanged(nameof(HasActiveJobs));
        OnPropertyChanged(nameof(CompactSummary));
        OnPropertyChanged(nameof(CompactDescription));
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(RetryWaitingCount));
        OnPropertyChanged(nameof(MaintenanceWaitingCount));
        OnPropertyChanged(nameof(PausedCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(IsMaintenanceBlocked));
        OnPropertyChanged(nameof(Summary));
        NotifyCommands();
        foreach (var direction in completedDirections) JobCompleted?.Invoke(direction);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = string.Empty;
            await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private void NotifyCommands()
    {
        CancelSelectedCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
        RetrySelectedCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        RetryOverwriteCommand.NotifyCanExecuteChanged();
        RetrySkipCommand.NotifyCanExecuteChanged();
        RetryRenameCommand.NotifyCanExecuteChanged();
        RemoveFinishedCommand.NotifyCanExecuteChanged();
        DiscardInterruptedCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.QueueChanged -= OnQueueChanged;
        _queue.QueueError -= OnQueueError;
        JobCompleted = null;
        await _queue.DisposeAsync();
    }
}
