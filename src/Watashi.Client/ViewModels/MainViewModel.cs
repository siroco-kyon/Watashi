using System.IO;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Client.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly EnumerationOptions RecursiveLocalOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly ApiClient _api;
    private readonly SessionManager _session;
    public LocalPaneViewModel Local { get; }
    public RemotePaneViewModel Remote { get; }
    public TransferViewModel Transfer { get; } = new();
    public TransferQueueViewModel TransferQueue { get; }
    public ThemeService Theme { get; }

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string latestStatusMessage = string.Empty;
    // 失敗/エラーを含むメッセージだけを目立つ赤バナーに昇格させる。空文字でバナー非表示。
    [ObservableProperty] private string errorMessage = string.Empty;
    private string? latestStatusSource;
    // 状態バーは一定時間で自動消去し、古いタイムスタンプが現在の操作のように残らないようにする。
    private readonly DispatcherTimer _statusClearTimer;
    private readonly object _refreshDebounceLock = new();
    private CancellationTokenSource? _remoteRefreshDebounce;
    private CancellationTokenSource? _localRefreshDebounce;
    public bool IsAdmin => _session.IsAdmin;
    public string? Username => _session.Username;
    public string ProtocolLabel { get; }
    public bool IsHttpConnection { get; }
    public string HttpTransportWarning => AppSettings.HttpTransportWarning;

    public MainViewModel(
        ApiClient api,
        SessionManager session,
        LocalPaneViewModel local,
        RemotePaneViewModel remote,
        AppSettings settings,
        ThemeService theme,
        TransferQueueViewModel transferQueue)
    {
        _api = api; _session = session; Local = local; Remote = remote; Theme = theme; TransferQueue = transferQueue;
        TransferQueue.JobCompleted += SchedulePaneRefresh;
        ProtocolLabel = settings.IsHttps ? "HTTPS" : settings.IsHttp ? "HTTP" : "不明";
        IsHttpConnection = settings.IsHttp;
        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _statusClearTimer.Tick += (_, _) =>
        {
            _statusClearTimer.Stop();
            latestStatusSource = null;
            LatestStatusMessage = string.Empty;
        };
        Local.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LocalPaneViewModel.StatusMessage))
                PromoteStatus("ローカル", Local.StatusMessage);
        };
        Remote.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemotePaneViewModel.StatusMessage))
                PromoteStatus("リモート", Remote.StatusMessage);
        };
    }

    public Task InitializeTransferQueueAsync(CancellationToken ct = default)
        => TransferQueue.InitializeAsync(ct);

    public async ValueTask DisposeTransferQueueAsync()
    {
        CancellationTokenSource? remote;
        CancellationTokenSource? local;
        lock (_refreshDebounceLock)
        {
            remote = _remoteRefreshDebounce;
            local = _localRefreshDebounce;
            _remoteRefreshDebounce = null;
            _localRefreshDebounce = null;
        }
        remote?.Cancel();
        local?.Cancel();
        remote?.Dispose();
        local?.Dispose();
        await TransferQueue.DisposeAsync();
    }

    private void SchedulePaneRefresh(string direction)
    {
        CancellationTokenSource current;
        lock (_refreshDebounceLock)
        {
            if (direction == TransferDirections.Upload)
            {
                _remoteRefreshDebounce?.Cancel();
                _remoteRefreshDebounce?.Dispose();
                current = _remoteRefreshDebounce = new CancellationTokenSource();
            }
            else
            {
                _localRefreshDebounce?.Cancel();
                _localRefreshDebounce?.Dispose();
                current = _localRefreshDebounce = new CancellationTokenSource();
            }
        }
        _ = RefreshPaneAfterQuietPeriodAsync(direction, current);
    }

    private async Task RefreshPaneAfterQuietPeriodAsync(string direction, CancellationTokenSource current)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), current.Token);
            if (direction == TransferDirections.Upload)
                await Remote.RefreshAsync();
            else
                await Local.RefreshAsync();
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ShowErrorBanner("転送完了後の一覧更新に失敗しました: " + ex.Message, ex);
        }
        finally
        {
            lock (_refreshDebounceLock)
            {
                if (direction == TransferDirections.Upload && ReferenceEquals(_remoteRefreshDebounce, current))
                    _remoteRefreshDebounce = null;
                else if (direction == TransferDirections.Download && ReferenceEquals(_localRefreshDebounce, current))
                    _localRefreshDebounce = null;
            }
            current.Dispose();
        }
    }

    partial void OnStatusMessageChanged(string value) => PromoteStatus("操作", value);

    private void PromoteStatus(string source, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            if (latestStatusSource == source)
            {
                latestStatusSource = null;
                LatestStatusMessage = string.Empty;
                _statusClearTimer.Stop();
            }
            return;
        }

        latestStatusSource = source;
        LatestStatusMessage = $"{DateTime.Now:HH:mm:ss} {source}: {message}";
        _statusClearTimer.Stop();
        _statusClearTimer.Start();

        if (ShouldShowErrorBanner(message))
            ShowErrorBanner($"{source}: {message}");
    }

    private static bool ShouldShowErrorBanner(string message) =>
        message.Contains("失敗", StringComparison.Ordinal) ||
        message.Contains("エラー", StringComparison.Ordinal) ||
        message.Contains("権限がありません", StringComparison.Ordinal) ||
        message.Contains("アクセスできません", StringComparison.Ordinal) ||
        message.Contains("拒否", StringComparison.Ordinal);

    /// <summary>
    /// 失敗/エラーを赤バナーに (再) 表示し、クライアントログにも残す。
    /// ErrorMessage は [ObservableProperty] のため同値の再代入では変更通知が出ず、
    /// ✕ で閉じた後に同じエラーが再発しても二度と表示されなかった。
    /// 一旦空にして「空→値」の遷移を作り、毎回確実に再点灯させる。
    /// </summary>
    private void ShowErrorBanner(string banner, Exception? ex = null)
    {
        if (ex is ApiException api)
            AppLog.Error($"{banner} (HTTP {(int)api.StatusCode})");
        else if (ex is not null)
            AppLog.Error(banner, ex);
        else
            AppLog.Error(banner);

        if (ErrorMessage == banner) ErrorMessage = string.Empty;
        ErrorMessage = banner;
    }

    /// <summary>
    /// アップロード/ダウンロードのエラーを確実に表示＋記録する。
    /// StatusMessage 経由 (OnStatusMessageChanged) は同値だと発火しないため、
    /// 操作系エラーはこの経路で直接バナー/ステータス/ログへ送る。
    /// </summary>
    private void ReportOperationError(string userMessage, Exception? ex = null)
    {
        latestStatusSource = "操作";
        LatestStatusMessage = $"{DateTime.Now:HH:mm:ss} 操作: {userMessage}";
        _statusClearTimer.Stop();
        _statusClearTimer.Start();
        ShowErrorBanner($"操作: {userMessage}", ex);
    }

    /// <summary>エラーバナーの ✕ ボタンから呼ばれ、バナーを閉じる。</summary>
    [RelayCommand]
    private void DismissError()
    {
        ErrorMessage = string.Empty;
        ClearErrorStatusForReplay();
    }

    private void ClearErrorStatusForReplay()
    {
        if (ShouldShowErrorBanner(StatusMessage))
            StatusMessage = string.Empty;
        if (ShouldShowErrorBanner(Local.StatusMessage))
            Local.StatusMessage = string.Empty;
        if (ShouldShowErrorBanner(Remote.StatusMessage))
            Remote.StatusMessage = string.Empty;
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    // ===== 転送制御 (キャンセル / 多重起動防止) =====
    private CancellationTokenSource? _transferCts;
    private CancellationToken TransferToken => _transferCts?.Token ?? CancellationToken.None;

    /// <summary>進行中の転送をキャンセルする。</summary>
    [RelayCommand]
    private void CancelTransfer() => _transferCts?.Cancel();

    /// <summary>転送開始。既に転送中なら false を返して多重起動を防ぐ。</summary>
    private bool TryBeginTransfer()
    {
        if (Transfer.IsActive)
        {
            StatusMessage = "別の転送が進行中です。完了までお待ちください。";
            return false;
        }
        _transferCts?.Dispose();
        _transferCts = new CancellationTokenSource();
        Transfer.IsActive = true;
        return true;
    }

    private void EndTransfer()
    {
        Transfer.IsActive = false;
        _transferCts?.Dispose();
        _transferCts = null;
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        Local.RefreshCommand.Execute(null);
        await Remote.RefreshAsync();
    }

    [RelayCommand]
    public async Task UploadAsync()
    {
        if (Local.Selected is null) { StatusMessage = "アップロードするローカルファイル/フォルダを選択してください。"; return; }
        if (Remote.SelectedLocation is null) { StatusMessage = "アップロード先のリモート場所を選択してください。"; return; }
        if (!Remote.SelectedLocation.Permissions.Write) { StatusMessage = "アップロード失敗: この場所には書き込み権限がありません。"; return; }
        if (Local.Selected.Type == FileEntryTypes.Parent) return;
        if (!TryBeginTransfer()) return;

        var location = Remote.SelectedLocation;
        var local = Path.Combine(Local.CurrentPath, Local.Selected.Name);
        var remote = RemotePaneViewModel.JoinPath(Remote.CurrentPath, Local.Selected.Name);

        try
        {
            if (Local.Selected.Type == FileEntryTypes.Directory)
            {
                var di = new DirectoryInfo(local);
                if (!di.Exists) { StatusMessage = "アップロード失敗: ローカルフォルダが見つかりません。"; return; }

                var existing = await FindRemoteEntryAsync(location.HostId, location.ShareId, remote);
                if (existing?.Type == FileEntryTypes.File)
                {
                    StatusMessage = "アップロード失敗: リモートに同名ファイルがあるためフォルダを作成できません。";
                    return;
                }

                var prompt = existing is null
                    ? $"フォルダ \"{Local.Selected.Name}\" をフォルダごとアップロードしますか？"
                    : $"リモートに同名フォルダがあります。\nフォルダを結合してアップロードしますか？";
                if (MessageBox.Show(prompt, "フォルダアップロード", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                var plan = BuildLocalUploadPlan(di, remote);
                var conflictPolicy = await ResolveUploadPolicyAsync(
                    location.HostId, location.ShareId, Remote.CurrentPath, plan.Files.Select(f => f.RemotePath));
                if (conflictPolicy is null) return;

                await UploadDirectoryAsync(di.Name, plan, remote, location.HostId, location.ShareId, conflictPolicy);
                StatusMessage = $"転送キューに追加しました: {Local.Selected.Name}";
                return;
            }

            if (Local.Selected.Type != FileEntryTypes.File) { StatusMessage = "アップロード対象がファイルまたはフォルダではありません。"; return; }
            var fi = new FileInfo(local);
            if (!fi.Exists) { StatusMessage = "アップロード失敗: ローカルファイルが見つかりません。"; return; }

            var remoteEntry = await FindRemoteEntryAsync(location.HostId, location.ShareId, remote);
            if (remoteEntry?.Type == FileEntryTypes.Directory)
            {
                StatusMessage = "アップロード失敗: リモートに同名フォルダがあるためファイルで上書きできません。";
                return;
            }
            var policy = remoteEntry is null
                ? TransferConflictPolicies.Ask
                : Views.TransferConflictDialog.Show(Application.Current?.MainWindow);
            if (policy is null) return;

            await UploadFileAsync(fi, remote, baseTransferred: 0, totalBytes: fi.Length,
                location.HostId, location.ShareId, policy);
            StatusMessage = $"転送キューに追加しました: {Local.Selected.Name}";
        }
        catch (OperationCanceledException) { StatusMessage = "アップロードをキャンセルしました。"; }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            ReportOperationError("アップロード失敗: 書き込み権限がありません。", ex);
        }
        catch (Exception ex) { ReportOperationError("アップロード失敗: " + ex.Message, ex); }
        finally { EndTransfer(); }
    }

    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (Remote.Selected is null) { StatusMessage = "ダウンロードするリモートファイル/フォルダを選択してください。"; return; }
        if (Remote.SelectedLocation is null) { StatusMessage = "ダウンロード元のリモート場所を選択してください。"; return; }
        if (Remote.Selected.Type == FileEntryTypes.Parent) return;
        if (Remote.Selected.IsReparsePoint)
        {
            StatusMessage = "ダウンロード失敗: リパースポイントは安全のためダウンロードできません。";
            return;
        }
        if (!Directory.Exists(Local.CurrentPath)) { StatusMessage = "ダウンロード失敗: ローカルフォルダが見つかりません。"; return; }
        if (!TryBeginTransfer()) return;

        var location = Remote.SelectedLocation;
        var destination = Path.Combine(Local.CurrentPath, Remote.Selected.Name);
        var remotePath = RemotePaneViewModel.JoinPath(Remote.CurrentPath, Remote.Selected.Name);

        try
        {
            if (Remote.Selected.Type == FileEntryTypes.Directory)
            {
                var exists = Directory.Exists(destination) || File.Exists(destination);
                var prompt = exists
                    ? $"ローカルに同名の項目があります。\nフォルダを結合してダウンロードしますか？"
                    : $"フォルダ \"{Remote.Selected.Name}\" をフォルダごとダウンロードしますか？";
                if (MessageBox.Show(prompt, "フォルダダウンロード", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                var plan = await BuildDownloadPlanAsync(remotePath, destination, location.HostId, location.ShareId);
                var conflictPolicy = ResolveDownloadPolicy(plan.Files.Select(f => f.LocalPath));
                if (conflictPolicy is null) return;

                await DownloadDirectoryAsync(plan, destination, location.HostId, location.ShareId, conflictPolicy);
                StatusMessage = $"転送キューに追加しました: {Remote.Selected.Name}";
                return;
            }

            if (Remote.Selected.Type != FileEntryTypes.File) { StatusMessage = "ダウンロード対象がファイルまたはフォルダではありません。"; return; }
            if (Directory.Exists(destination))
            {
                StatusMessage = "ダウンロード失敗: ローカルに同名フォルダがあるためファイルで上書きできません。";
                return;
            }
            var policy = File.Exists(destination)
                ? Views.TransferConflictDialog.Show(Application.Current?.MainWindow)
                : TransferConflictPolicies.Ask;
            if (policy is null) return;

            var selectedSize = Remote.Selected.Size ?? 0;
            await DownloadFileAsync(remotePath, destination, expectedFileSize: selectedSize,
                baseTransferred: 0, aggregateTotalBytes: selectedSize,
                location.HostId, location.ShareId, policy);
            StatusMessage = $"転送キューに追加しました: {Remote.Selected.Name}";
        }
        catch (OperationCanceledException) { StatusMessage = "ダウンロードをキャンセルしました。"; }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            ReportOperationError("ダウンロード失敗: 読み取り権限がありません。", ex);
        }
        catch (Exception ex) { ReportOperationError("ダウンロード失敗: " + ex.Message, ex); }
        finally { EndTransfer(); }
    }

    /// <summary>
    /// 複数選択された項目を一括アップロード。1 件以下なら従来の詳細プロンプト付き <see cref="UploadAsync"/> に委譲。
    /// 2 件以上は冒頭で一度だけ確認し、競合方針を一括指定する。
    /// </summary>
    public async Task UploadManyAsync(IReadOnlyList<FileEntry>? items)
    {
        var targets = (items ?? Array.Empty<FileEntry>())
            .Where(i => i is not null && i.Type != FileEntryTypes.Parent).ToList();
        if (targets.Count <= 1) { await UploadAsync(); return; }

        var paths = targets.Select(t => Path.Combine(Local.CurrentPath, t.Name)).ToList();
        await UploadLocalPathsAsync(
            paths,
            confirmMessage: $"{targets.Count} 件を転送キューへ追加します。よろしいですか？",
            completedMessage: $"転送キューに追加しました: {targets.Count} 件");
    }

    /// <summary>
    /// 任意のローカルパス群を現在のリモートフォルダ直下へアップロードする共通処理。
    /// 一括選択アップロードとドラッグ＆ドロップ (外部エクスプローラからの投下を含む) の双方から使う。
    /// </summary>
    public async Task UploadLocalPathsAsync(IReadOnlyList<string> localPaths, string confirmMessage, string completedMessage)
    {
        if (Remote.SelectedLocation is null) { StatusMessage = "アップロード先のリモート場所を選択してください。"; return; }
        if (!Remote.SelectedLocation.Permissions.Write) { StatusMessage = "アップロード失敗: この場所には書き込み権限がありません。"; return; }
        var paths = (localPaths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0) { StatusMessage = "アップロード対象がありません。"; return; }
        if (!string.IsNullOrEmpty(confirmMessage))
        {
            var owner = Application.Current?.MainWindow;
            var result = owner is null
                ? MessageBox.Show(confirmMessage, "アップロード", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(owner, confirmMessage, "アップロード", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }
        if (!TryBeginTransfer()) return;

        var location = Remote.SelectedLocation;
        try
        {
            var remoteDirs = new List<string>();
            var files = new List<(FileInfo File, string RemotePath)>();
            foreach (var p in paths)
            {
                var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) continue;
                var remotePath = RemotePaneViewModel.JoinPath(Remote.CurrentPath, name);
                if (Directory.Exists(p))
                {
                    var di = new DirectoryInfo(p);
                    remoteDirs.Add(remotePath);
                    foreach (var d in di.EnumerateDirectories("*", RecursiveLocalOptions))
                        remoteDirs.Add(JoinRemotePath(remotePath, ToRemoteRelativePath(di.FullName, d.FullName)));
                    foreach (var f in di.EnumerateFiles("*", RecursiveLocalOptions))
                        files.Add((f, JoinRemotePath(remotePath, ToRemoteRelativePath(di.FullName, f.FullName))));
                }
                else if (File.Exists(p))
                {
                    files.Add((new FileInfo(p), remotePath));
                }
            }
            if (files.Count == 0 && remoteDirs.Count == 0) { StatusMessage = "アップロード対象が見つかりません。"; return; }

            var conflictPolicy = await ResolveUploadPolicyAsync(
                location.HostId, location.ShareId, Remote.CurrentPath, files.Select(f => f.RemotePath));
            if (conflictPolicy is null) return;

            Transfer.FileName = Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Transfer.TotalBytes = files.Sum(f => f.File.Length);
            Transfer.BytesTransferred = 0;

            foreach (var dir in remoteDirs.Distinct().OrderBy(x => x.Count(c => c == '/')))
                await EnsureRemoteDirectoryAsync(dir, location.HostId, location.ShareId);

            await TransferQueue.EnqueueUploadsAsync(
                files.Select(item => new UploadQueueRequest(
                    item.File.FullName, location.HostId, location.ShareId,
                    item.RemotePath, conflictPolicy)),
                TransferToken);
            StatusMessage = completedMessage;
        }
        catch (OperationCanceledException) { StatusMessage = "アップロードをキャンセルしました。"; }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { ReportOperationError("アップロード失敗: 書き込み権限がありません。", ex); }
        catch (Exception ex) { ReportOperationError("アップロード失敗: " + ex.Message, ex); }
        finally { EndTransfer(); }
    }

    /// <summary>
    /// 複数選択された項目を一括ダウンロード。1 件以下なら従来の <see cref="DownloadAsync"/> に委譲。
    /// </summary>
    public async Task DownloadManyAsync(IReadOnlyList<FileEntry>? items)
    {
        var targets = (items ?? Array.Empty<FileEntry>())
            .Where(i => i is not null && i.Type != FileEntryTypes.Parent).ToList();
        if (targets.Count <= 1) { await DownloadAsync(); return; }
        if (Remote.SelectedLocation is null) { StatusMessage = "ダウンロード元のリモート場所を選択してください。"; return; }
        if (!Directory.Exists(Local.CurrentPath)) { StatusMessage = "ダウンロード失敗: ローカルフォルダが見つかりません。"; return; }
        if (targets.Any(e => e.IsReparsePoint))
        {
            StatusMessage = "ダウンロード失敗: リパースポイントは安全のためダウンロードできません。";
            return;
        }
        if (MessageBox.Show(
                $"{targets.Count} 件を転送キューへ追加します。よろしいですか？",
                "ダウンロード", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!TryBeginTransfer()) return;

        var location = Remote.SelectedLocation;
        var remoteBasePath = Remote.CurrentPath;
        var localBasePath = Local.CurrentPath;
        try
        {
            var directories = new List<string>();
            var files = new List<RemoteDownloadItem>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in targets)
            {
                var remotePath = RemotePaneViewModel.JoinPath(remoteBasePath, entry.Name);
                var destination = Path.Combine(localBasePath, entry.Name);
                if (entry.Type == FileEntryTypes.Directory)
                    await BuildDownloadPlanAsync(remotePath, destination, directories, files, visited, location.HostId, location.ShareId);
                else if (entry.Type == FileEntryTypes.File)
                    files.Add(new RemoteDownloadItem(remotePath, destination, entry.Size ?? 0));
            }

            var conflictPolicy = ResolveDownloadPolicy(files.Select(f => f.LocalPath));
            if (conflictPolicy is null) return;

            Transfer.FileName = $"{targets.Count} 件";
            Transfer.TotalBytes = files.Sum(f => f.Size);
            Transfer.BytesTransferred = 0;

            foreach (var dir in directories) EnsureLocalDirectory(dir);
            await TransferQueue.EnqueueDownloadsAsync(
                files.Select(item => new DownloadQueueRequest(
                    location.HostId, location.ShareId, item.RemotePath,
                    item.LocalPath, item.Size, conflictPolicy)),
                TransferToken);
            StatusMessage = $"転送キューに追加しました: {targets.Count} 件";
        }
        catch (OperationCanceledException) { StatusMessage = "ダウンロードをキャンセルしました。"; }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { ReportOperationError("ダウンロード失敗: 読み取り権限がありません。", ex); }
        catch (Exception ex) { ReportOperationError("ダウンロード失敗: " + ex.Message, ex); }
        finally { EndTransfer(); }
    }

    /// <summary>
    /// アップロードの競合方針を決める。転送先に同名項目が無ければダイアログを出さず
    /// <see cref="TransferConflictPolicies.Ask"/> を返す (万一あとから同名が現れてもジョブが停止して確認される)。
    /// 戻り値 null はユーザーがダイアログをキャンセルしたことを示す。
    /// </summary>
    private async Task<string?> ResolveUploadPolicyAsync(
        int hostId, int shareId, string baseRemoteDir, IEnumerable<string> plannedRemotePaths)
    {
        bool hasConflict;
        try
        {
            StatusMessage = "リモートの同名項目を確認しています…";
            hasConflict = await TransferConflictPlanner.HasRemoteConflictAsync(
                baseRemoteDir, plannedRemotePaths,
                dir => ListAllRemoteEntriesAsync(hostId, shareId, dir));
        }
        catch (Exception ex)
        {
            // 確認できないときは安全側に倒し、従来どおり方針をユーザーに選ばせる。
            AppLog.Info("同名項目の事前確認に失敗したため競合方針を確認します: " + ex.Message);
            hasConflict = true;
        }
        return hasConflict
            ? Views.TransferConflictDialog.Show(Application.Current?.MainWindow)
            : TransferConflictPolicies.Ask;
    }

    /// <summary>
    /// ダウンロードの競合方針を決める。保存先に同名のファイル/フォルダが無ければダイアログを出さない。
    /// 戻り値 null はユーザーがダイアログをキャンセルしたことを示す。
    /// </summary>
    private static string? ResolveDownloadPolicy(IEnumerable<string> plannedLocalPaths)
        => TransferConflictPlanner.HasLocalConflict(plannedLocalPaths)
            ? Views.TransferConflictDialog.Show(Application.Current?.MainWindow)
            : TransferConflictPolicies.Ask;

    /// <summary>
    /// フォルダ配下を再帰的に列挙し、リモートの絶対パスへ対応付けた転送計画を作る。
    /// 競合判定 (<see cref="ResolveUploadPolicyAsync"/>) とキュー投入で同じ計画を使い、二重列挙を避ける。
    /// </summary>
    private static LocalUploadPlan BuildLocalUploadPlan(DirectoryInfo source, string remoteRoot)
    {
        var directories = source.EnumerateDirectories("*", RecursiveLocalOptions)
            .Select(d => JoinRemotePath(remoteRoot, ToRemoteRelativePath(source.FullName, d.FullName)))
            .OrderBy(x => x.Count(c => c == '/'))
            .ToList();
        var files = source.EnumerateFiles("*", RecursiveLocalOptions)
            .Select(f => new LocalUploadItem(f, JoinRemotePath(remoteRoot, ToRemoteRelativePath(source.FullName, f.FullName))))
            .ToList();
        return new LocalUploadPlan(directories, files);
    }

    private async Task UploadDirectoryAsync(
        string displayName, LocalUploadPlan plan, string remoteRoot, int hostId, int shareId, string conflictPolicy)
    {
        Transfer.FileName = displayName;
        Transfer.TotalBytes = plan.Files.Sum(f => f.File.Length);
        Transfer.BytesTransferred = 0;
        Transfer.IsActive = true;

        await EnsureRemoteDirectoryAsync(remoteRoot, hostId, shareId);
        foreach (var dir in plan.RemoteDirectories)
            await EnsureRemoteDirectoryAsync(dir, hostId, shareId);

        await TransferQueue.EnqueueUploadsAsync(
            plan.Files.Select(item => new UploadQueueRequest(
                item.File.FullName, hostId, shareId, item.RemotePath, conflictPolicy)),
            TransferToken);
    }

    private async Task UploadFileAsync(
        FileInfo file, string remotePath, long baseTransferred, long totalBytes,
        int hostId, int shareId, string conflictPolicy)
    {
        Transfer.FileName = file.Name;
        Transfer.TotalBytes = totalBytes;
        Transfer.BytesTransferred = baseTransferred;
        Transfer.IsActive = true;

        await TransferQueue.EnqueueUploadAsync(
            file.FullName, hostId, shareId, remotePath, conflictPolicy, TransferToken);
    }

    /// <summary>
    /// リモートフォルダ配下を再帰的に走査し、ローカルの保存先へ対応付けた転送計画を作る。
    /// 競合判定 (<see cref="ResolveDownloadPolicy"/>) とキュー投入で同じ計画を使い、二重走査を避ける。
    /// </summary>
    private async Task<RemoteDownloadPlan> BuildDownloadPlanAsync(
        string remoteRoot, string localRoot, int hostId, int shareId)
    {
        var directories = new List<string>();
        var files = new List<RemoteDownloadItem>();
        await BuildDownloadPlanAsync(remoteRoot, localRoot, directories, files, new HashSet<string>(StringComparer.OrdinalIgnoreCase), hostId, shareId);
        return new RemoteDownloadPlan(directories, files);
    }

    private async Task DownloadDirectoryAsync(
        RemoteDownloadPlan plan, string localRoot, int hostId, int shareId, string conflictPolicy)
    {
        Transfer.FileName = Path.GetFileName(localRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Transfer.TotalBytes = plan.Files.Sum(f => f.Size);
        Transfer.BytesTransferred = 0;
        Transfer.IsActive = true;

        foreach (var dir in plan.Directories)
            EnsureLocalDirectory(dir);

        await TransferQueue.EnqueueDownloadsAsync(
            plan.Files.Select(item => new DownloadQueueRequest(
                hostId, shareId, item.RemotePath, item.LocalPath, item.Size, conflictPolicy)),
            TransferToken);
    }

    private async Task DownloadFileAsync(
        string remotePath, string destination, long expectedFileSize,
        long baseTransferred, long aggregateTotalBytes,
        int hostId, int shareId, string conflictPolicy)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) EnsureLocalDirectory(parent);

        Transfer.FileName = Path.GetFileName(destination);
        Transfer.TotalBytes = aggregateTotalBytes;
        Transfer.BytesTransferred = baseTransferred;
        Transfer.IsActive = true;

        await TransferQueue.EnqueueDownloadAsync(
            hostId, shareId, remotePath, destination, expectedFileSize, conflictPolicy, TransferToken);
    }

    private async Task BuildDownloadPlanAsync(
        string remoteDir,
        string localDir,
        List<string> directories,
        List<RemoteDownloadItem> files,
        HashSet<string> visited,
        int hostId,
        int shareId)
    {
        var normalized = PathHelper.NormalizePath(remoteDir);
        if (!visited.Add(normalized)) return;

        directories.Add(localDir);
        var entries = await ListAllRemoteEntriesAsync(hostId, shareId, normalized);
        var reparsePoint = entries.FirstOrDefault(e => e.IsReparsePoint);
        if (reparsePoint is not null)
            throw new IOException($"リパースポイントは安全のためダウンロードできません: {JoinRemotePath(normalized, reparsePoint.Name)}");
        foreach (var dir in entries.Where(e => e.Type == FileEntryTypes.Directory))
        {
            var childRemote = JoinRemotePath(normalized, dir.Name);
            var childLocal = Path.Combine(localDir, dir.Name);
            await BuildDownloadPlanAsync(childRemote, childLocal, directories, files, visited, hostId, shareId);
        }
        foreach (var file in entries.Where(e => e.Type == FileEntryTypes.File))
        {
            files.Add(new RemoteDownloadItem(
                JoinRemotePath(normalized, file.Name),
                Path.Combine(localDir, file.Name),
                file.Size ?? 0));
        }
    }

    private void EnsureLocalDirectory(string path)
    {
        if (File.Exists(path))
            throw new IOException($"同名ファイルがあるためフォルダを作成できません: {path}");
        Directory.CreateDirectory(path);
    }

    private async Task EnsureRemoteDirectoryAsync(string remotePath, int hostId, int shareId)
    {
        var normalized = PathHelper.NormalizePath(remotePath);
        if (normalized == "/") return;

        // まず mkdir を試す (新規フォルダが大半)。失敗したときだけ一覧で状態を確認する。
        // 事前に毎回親フォルダを全件取得すると、サブフォルダの多いアップロードが非常に遅くなる。
        try
        {
            await _api.MkdirAsync(hostId, shareId, normalized);
        }
        catch (ApiException ex) when (ex.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
        {
            var existing = await FindRemoteEntryAsync(hostId, shareId, normalized);
            if (existing?.Type == FileEntryTypes.Directory) return; // 既存フォルダ: 結合として続行
            if (existing is not null)
                throw new IOException($"リモートに同名ファイルがあるためフォルダを作成できません: {normalized}");
            throw;
        }
    }

    private async Task<FileEntry?> FindRemoteEntryAsync(int hostId, int shareId, string remotePath)
    {
        var normalized = PathHelper.NormalizePath(remotePath);
        if (normalized == "/") return new FileEntry { Name = "/", Type = FileEntryTypes.Directory };

        var parent = PathHelper.GetParent(normalized);
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        var entries = await ListAllRemoteEntriesAsync(hostId, shareId, parent);
        return entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<FileEntry>> ListAllRemoteEntriesAsync(int hostId, int shareId, string path)
    {
        // page=0 で全件を一括取得する。旧サーバ (page=0 を page=1 扱い) の場合のみ
        // page=2 以降を追加取得する。詳細は RemotePaneViewModel.RefreshAsync を参照。
        var all = new List<FileEntry>();
        for (var page = 0; ; page = page == 0 ? 2 : page + 1)
        {
            var res = await _api.ListFilesAsync(hostId, shareId, path, page);
            var entries = res.Entries.Where(e => e.Type != FileEntryTypes.Parent).ToList();
            all.AddRange(entries);
            if (all.Count >= res.TotalCount || entries.Count == 0) break;
        }
        return all;
    }

    private static string JoinRemotePath(string parent, string relativePath)
    {
        var child = relativePath.Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(child) ? PathHelper.NormalizePath(parent) : RemotePaneViewModel.JoinPath(parent, child);
    }

    private static string ToRemoteRelativePath(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private record LocalUploadItem(FileInfo File, string RemotePath);
    private record LocalUploadPlan(IReadOnlyList<string> RemoteDirectories, IReadOnlyList<LocalUploadItem> Files);
    private record RemoteDownloadItem(string RemotePath, string LocalPath, long Size);
    private record RemoteDownloadPlan(IReadOnlyList<string> Directories, IReadOnlyList<RemoteDownloadItem> Files);
}
