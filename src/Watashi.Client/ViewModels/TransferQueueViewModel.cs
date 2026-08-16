using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Helpers;

namespace Watashi.Client.ViewModels;

public sealed class TransferQueueItemViewModel
{
    public TransferJobRecord Job { get; }
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
    public string ProgressText => $"{FormatBytes(Job.BytesTransferred)} / {FormatBytes(Job.TotalBytes)} ({Percent}%)";
    public string SourceText => Job.Direction == TransferDirections.Upload ? Job.LocalPath : Job.RemotePath;
    public string DestinationText => Job.Direction == TransferDirections.Upload ? Job.RemotePath : Job.LocalPath;
    public string? ErrorText => Job.LastError;
    public bool IsRunning => Job.State is TransferJobStates.Running or TransferJobStates.Canceling;
    public bool CanCancel => Job.State is TransferJobStates.Queued or TransferJobStates.Running or
        TransferJobStates.RetryWaiting or TransferJobStates.Paused;
    public bool CanRetry => Job.State == TransferJobStates.Failed;
    public bool CanResume => Job.State is TransferJobStates.Paused or TransferJobStates.Canceled;

    public TransferQueueItemViewModel(TransferJobRecord job) => Job = job;

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
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
    public event Action<string>? JobCompleted;
    [ObservableProperty] private TransferQueueItemViewModel? selectedJob;
    [ObservableProperty] private string errorMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    public bool HasJobs => Jobs.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasFailed => Jobs.Any(x => x.Job.State == TransferJobStates.Failed);
    public bool HasPending => Jobs.Any(x => x.Job.State is TransferJobStates.Queued or
        TransferJobStates.Running or TransferJobStates.Canceling or
        TransferJobStates.RetryWaiting or TransferJobStates.Paused);
    public int ActiveCount => Jobs.Count(x => x.Job.State is TransferJobStates.Running or TransferJobStates.Canceling);
    public int QueuedCount => Jobs.Count(x => x.Job.State == TransferJobStates.Queued);
    public int FailedCount => Jobs.Count(x => x.Job.State == TransferJobStates.Failed);
    public string Summary => HasJobs
        ? $"転送: 実行中 {ActiveCount} / 待機 {QueuedCount} / 失敗 {FailedCount}"
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

    public TransferQueueViewModel(TransferQueueService queue)
    {
        _queue = queue;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _queue.QueueChanged += OnQueueChanged;
        _queue.QueueError += OnQueueError;
        CancelSelectedCommand = new AsyncRelayCommand(CancelSelectedAsync, () => SelectedJob?.CanCancel == true);
        CancelAllCommand = new AsyncRelayCommand(() => RunAsync(_queue.CancelAllAsync), () => HasPending);
        RetrySelectedCommand = new AsyncRelayCommand(RetrySelectedAsync, () => SelectedJob?.CanRetry == true);
        RetryFailedCommand = new AsyncRelayCommand(() => RunAsync(_queue.RetryFailedAsync), () => HasFailed);
        ResumeSelectedCommand = new AsyncRelayCommand(ResumeSelectedAsync, () => SelectedJob?.CanResume == true);
        RetryOverwriteCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(TransferConflictPolicies.Overwrite), () => SelectedJob?.CanRetry == true);
        RetrySkipCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(TransferConflictPolicies.Skip), () => SelectedJob?.CanRetry == true);
        RetryRenameCommand = new AsyncRelayCommand(
            () => ResolveSelectedAsync(TransferConflictPolicies.Rename), () => SelectedJob?.CanRetry == true);
        RemoveFinishedCommand = new AsyncRelayCommand(
            () => RunAsync(_queue.RemoveFinishedAsync), () => Jobs.Any(x =>
                x.Job.State is TransferJobStates.Completed or TransferJobStates.Failed or
                    TransferJobStates.Canceled or TransferJobStates.Skipped));
    }

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
        Jobs.Clear();
        foreach (var job in snapshot.OrderByDescending(x => x.CreatedAt))
            Jobs.Add(new TransferQueueItemViewModel(job));
        _knownStates.Clear();
        foreach (var job in snapshot) _knownStates[job.Id] = job.State;
        SelectedJob = selectedId is null ? Jobs.FirstOrDefault() : Jobs.FirstOrDefault(x => x.Id == selectedId);
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(FailedCount));
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
