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

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string latestStatusMessage = string.Empty;
    // 失敗/エラーを含むメッセージだけを目立つ赤バナーに昇格させる。空文字でバナー非表示。
    [ObservableProperty] private string errorMessage = string.Empty;
    private string? latestStatusSource;
    // 状態バーは一定時間で自動消去し、古いタイムスタンプが現在の操作のように残らないようにする。
    private readonly DispatcherTimer _statusClearTimer;
    public bool IsAdmin => _session.IsAdmin;
    public string? Username => _session.Username;
    public string ProtocolLabel { get; }

    public MainViewModel(ApiClient api, SessionManager session, LocalPaneViewModel local, RemotePaneViewModel remote, AppSettings settings)
    {
        _api = api; _session = session; Local = local; Remote = remote;
        ProtocolLabel = settings.IsHttps ? "HTTPS" : "HTTP";
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

        if (message.Contains("失敗") || message.Contains("エラー"))
            ShowErrorBanner($"{source}: {message}");
    }

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
    private void DismissError() => ErrorMessage = string.Empty;

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
                    : $"リモートに同名フォルダがあります。\nフォルダを結合し、同名ファイルは上書きしてアップロードしますか？";
                if (MessageBox.Show(prompt, "フォルダアップロード", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                await UploadDirectoryAsync(di, remote, location.HostId, location.ShareId);
                await Remote.RefreshAsync();
                StatusMessage = $"フォルダアップロード完了: {Local.Selected.Name}";
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
            if (remoteEntry is not null)
            {
                var overwrite = MessageBox.Show(
                    $"{Local.Selected.Name} はリモートに既に存在します。上書きしますか？",
                    "アップロード",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes) return;
            }

            await UploadFileAsync(fi, remote, baseTransferred: 0, totalBytes: fi.Length, location.HostId, location.ShareId);
            await Remote.RefreshAsync();
            StatusMessage = $"アップロード完了: {Local.Selected.Name}";
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
                    ? $"ローカルに同名の項目があります。\nフォルダを結合し、同名ファイルは上書きしてダウンロードしますか？"
                    : $"フォルダ \"{Remote.Selected.Name}\" をフォルダごとダウンロードしますか？";
                if (MessageBox.Show(prompt, "フォルダダウンロード", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                await DownloadDirectoryAsync(remotePath, destination, location.HostId, location.ShareId);
                await Local.RefreshAsync();
                StatusMessage = $"フォルダダウンロード完了: {Remote.Selected.Name}";
                return;
            }

            if (Remote.Selected.Type != FileEntryTypes.File) { StatusMessage = "ダウンロード対象がファイルまたはフォルダではありません。"; return; }
            if (Directory.Exists(destination))
            {
                StatusMessage = "ダウンロード失敗: ローカルに同名フォルダがあるためファイルで上書きできません。";
                return;
            }
            if (File.Exists(destination))
            {
                var overwrite = MessageBox.Show(
                    $"{Remote.Selected.Name} はローカルフォルダに既に存在します。上書きしますか？",
                    "ダウンロード",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes) return;
            }

            await DownloadFileAsync(remotePath, destination, baseTransferred: 0, totalBytes: Remote.Selected.Size ?? 0, location.HostId, location.ShareId);
            await Local.RefreshAsync();
            StatusMessage = $"ダウンロード完了: {Remote.Selected.Name}";
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
    /// 2 件以上は冒頭で一度だけ確認し、同名は上書きで進める。
    /// </summary>
    public async Task UploadManyAsync(IReadOnlyList<FileEntry>? items)
    {
        var targets = (items ?? Array.Empty<FileEntry>())
            .Where(i => i is not null && i.Type != FileEntryTypes.Parent).ToList();
        if (targets.Count <= 1) { await UploadAsync(); return; }

        var paths = targets.Select(t => Path.Combine(Local.CurrentPath, t.Name)).ToList();
        await UploadLocalPathsAsync(
            paths,
            confirmMessage: $"{targets.Count} 件をアップロードします。\nリモートの同名項目は上書きされます。よろしいですか？",
            completedMessage: $"アップロード完了: {targets.Count} 件");
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
        if (!string.IsNullOrEmpty(confirmMessage) &&
            MessageBox.Show(confirmMessage, "アップロード", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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

            Transfer.FileName = Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Transfer.TotalBytes = files.Sum(f => f.File.Length);
            Transfer.BytesTransferred = 0;

            foreach (var dir in remoteDirs.Distinct().OrderBy(x => x.Count(c => c == '/')))
                await EnsureRemoteDirectoryAsync(dir, location.HostId, location.ShareId);

            long completed = 0;
            foreach (var item in files)
            {
                await UploadFileAsync(item.File, item.RemotePath, completed, Transfer.TotalBytes, location.HostId, location.ShareId);
                completed += item.File.Length;
                Transfer.BytesTransferred = completed;
            }
            await Remote.RefreshAsync();
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
        if (MessageBox.Show(
                $"{targets.Count} 件をダウンロードします。\nローカルの同名項目は上書きされます。よろしいですか？",
                "ダウンロード", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!TryBeginTransfer()) return;

        var location = Remote.SelectedLocation;
        try
        {
            var directories = new List<string>();
            var files = new List<RemoteDownloadItem>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in targets)
            {
                var remotePath = RemotePaneViewModel.JoinPath(Remote.CurrentPath, entry.Name);
                var destination = Path.Combine(Local.CurrentPath, entry.Name);
                if (entry.Type == FileEntryTypes.Directory)
                    await BuildDownloadPlanAsync(remotePath, destination, directories, files, visited, location.HostId, location.ShareId);
                else if (entry.Type == FileEntryTypes.File)
                    files.Add(new RemoteDownloadItem(remotePath, destination, entry.Size ?? 0));
            }

            Transfer.FileName = $"{targets.Count} 件";
            Transfer.TotalBytes = files.Sum(f => f.Size);
            Transfer.BytesTransferred = 0;

            foreach (var dir in directories) EnsureLocalDirectory(dir);
            long completed = 0;
            foreach (var item in files)
            {
                await DownloadFileAsync(item.RemotePath, item.LocalPath, completed, Transfer.TotalBytes, location.HostId, location.ShareId);
                completed += item.Size;
                Transfer.BytesTransferred = completed;
            }
            await Local.RefreshAsync();
            StatusMessage = $"ダウンロード完了: {targets.Count} 件";
        }
        catch (OperationCanceledException) { StatusMessage = "ダウンロードをキャンセルしました。"; }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { ReportOperationError("ダウンロード失敗: 読み取り権限がありません。", ex); }
        catch (Exception ex) { ReportOperationError("ダウンロード失敗: " + ex.Message, ex); }
        finally { EndTransfer(); }
    }

    private async Task UploadDirectoryAsync(DirectoryInfo source, string remoteRoot, int hostId, int shareId)
    {
        var files = source.EnumerateFiles("*", RecursiveLocalOptions)
            .Select(f => new LocalUploadItem(f, ToRemoteRelativePath(source.FullName, f.FullName)))
            .ToList();
        var directories = source.EnumerateDirectories("*", RecursiveLocalOptions)
            .Select(d => ToRemoteRelativePath(source.FullName, d.FullName))
            .OrderBy(x => x.Count(c => c == '/'))
            .ToList();

        Transfer.FileName = source.Name;
        Transfer.TotalBytes = files.Sum(f => f.File.Length);
        Transfer.BytesTransferred = 0;
        Transfer.IsActive = true;

        await EnsureRemoteDirectoryAsync(remoteRoot, hostId, shareId);
        foreach (var relativeDir in directories)
            await EnsureRemoteDirectoryAsync(JoinRemotePath(remoteRoot, relativeDir), hostId, shareId);

        long completed = 0;
        foreach (var item in files)
        {
            var remotePath = JoinRemotePath(remoteRoot, item.RelativePath);
            await UploadFileAsync(item.File, remotePath, completed, Transfer.TotalBytes, hostId, shareId);
            completed += item.File.Length;
            Transfer.BytesTransferred = completed;
        }
    }

    private async Task UploadFileAsync(FileInfo file, string remotePath, long baseTransferred, long totalBytes, int hostId, int shareId)
    {
        Transfer.FileName = file.Name;
        Transfer.TotalBytes = totalBytes;
        Transfer.BytesTransferred = baseTransferred;
        Transfer.IsActive = true;

        await using var fs = File.OpenRead(file.FullName);
        var progress = new Progress<long>(b => Transfer.BytesTransferred = baseTransferred + b);
        await _api.UploadAsync(hostId, shareId, remotePath, fs, file.Length, progress, TransferToken);
    }

    private async Task DownloadDirectoryAsync(string remoteRoot, string localRoot, int hostId, int shareId)
    {
        var directories = new List<string>();
        var files = new List<RemoteDownloadItem>();
        await BuildDownloadPlanAsync(remoteRoot, localRoot, directories, files, new HashSet<string>(StringComparer.OrdinalIgnoreCase), hostId, shareId);

        Transfer.FileName = Path.GetFileName(localRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Transfer.TotalBytes = files.Sum(f => f.Size);
        Transfer.BytesTransferred = 0;
        Transfer.IsActive = true;

        foreach (var dir in directories)
            EnsureLocalDirectory(dir);

        long completed = 0;
        foreach (var item in files)
        {
            await DownloadFileAsync(item.RemotePath, item.LocalPath, completed, Transfer.TotalBytes, hostId, shareId);
            completed += item.Size;
            Transfer.BytesTransferred = completed;
        }
    }

    private async Task DownloadFileAsync(string remotePath, string destination, long baseTransferred, long totalBytes, int hostId, int shareId)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) EnsureLocalDirectory(parent);

        Transfer.FileName = Path.GetFileName(destination);
        Transfer.TotalBytes = totalBytes;
        Transfer.BytesTransferred = baseTransferred;
        Transfer.IsActive = true;

        var tempPath = destination + ".part";
        bool completed = false;
        try
        {
            var progress = new Progress<long>(b => Transfer.BytesTransferred = baseTransferred + b);
            await using (var fs = File.Create(tempPath))
            {
                await _api.DownloadAsync(hostId, shareId, remotePath, fs, progress, TransferToken);
                await fs.FlushAsync();
            }
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempPath, destination);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
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
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(path);
    }

    private async Task EnsureRemoteDirectoryAsync(string remotePath, int hostId, int shareId)
    {
        var normalized = PathHelper.NormalizePath(remotePath);
        if (normalized == "/") return;

        var existing = await FindRemoteEntryAsync(hostId, shareId, normalized);
        if (existing?.Type == FileEntryTypes.Directory) return;
        if (existing is not null)
            throw new IOException($"リモートに同名ファイルがあるためフォルダを作成できません: {normalized}");

        await _api.MkdirAsync(hostId, shareId, normalized);
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
        var all = new List<FileEntry>();
        for (var page = 1; ; page++)
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

    private record LocalUploadItem(FileInfo File, string RelativePath);
    private record RemoteDownloadItem(string RemotePath, string LocalPath, long Size);
}
