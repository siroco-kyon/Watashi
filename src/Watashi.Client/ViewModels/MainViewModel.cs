using System.IO;
using System.Net;
using System.Windows;
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
    private string? latestStatusSource;
    public bool IsAdmin => _session.IsAdmin;
    public string? Username => _session.Username;
    public string ProtocolLabel { get; }

    public MainViewModel(ApiClient api, SessionManager session, LocalPaneViewModel local, RemotePaneViewModel remote, AppSettings settings)
    {
        _api = api; _session = session; Local = local; Remote = remote;
        ProtocolLabel = settings.IsHttps ? "HTTPS" : "HTTP";
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
            }
            return;
        }

        latestStatusSource = source;
        LatestStatusMessage = $"{DateTime.Now:HH:mm:ss} {source}: {message}";
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
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "アップロード失敗: 書き込み権限がありません。";
        }
        catch (Exception ex) { StatusMessage = "アップロード失敗: " + ex.Message; }
        finally { Transfer.IsActive = false; }
    }

    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (Remote.Selected is null) { StatusMessage = "ダウンロードするリモートファイル/フォルダを選択してください。"; return; }
        if (Remote.SelectedLocation is null) { StatusMessage = "ダウンロード元のリモート場所を選択してください。"; return; }
        if (Remote.Selected.Type == FileEntryTypes.Parent) return;
        if (!Directory.Exists(Local.CurrentPath)) { StatusMessage = "ダウンロード失敗: ローカルフォルダが見つかりません。"; return; }

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
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "ダウンロード失敗: 読み取り権限がありません。";
        }
        catch (Exception ex) { StatusMessage = "ダウンロード失敗: " + ex.Message; }
        finally { Transfer.IsActive = false; }
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
        await _api.UploadAsync(hostId, shareId, remotePath, fs, file.Length, progress);
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
                await _api.DownloadAsync(hostId, shareId, remotePath, fs, progress);
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
