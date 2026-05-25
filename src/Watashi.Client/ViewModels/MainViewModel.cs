using System.IO;
using System.Net;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;

namespace Watashi.Client.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly SessionManager _session;
    public LocalPaneViewModel Local { get; }
    public RemotePaneViewModel Remote { get; }
    public TransferViewModel Transfer { get; } = new();

    [ObservableProperty] private string statusMessage = string.Empty;
    public bool IsAdmin => _session.IsAdmin;
    public string? Username => _session.Username;
    public string ProtocolLabel { get; }

    public MainViewModel(ApiClient api, SessionManager session, LocalPaneViewModel local, RemotePaneViewModel remote, AppSettings settings)
    {
        _api = api; _session = session; Local = local; Remote = remote;
        ProtocolLabel = settings.IsHttps ? "HTTPS" : "HTTP";
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
        if (Local.Selected is null) { StatusMessage = "アップロードするローカルファイルを選択してください。"; return; }
        if (Remote.SelectedLocation is null) { StatusMessage = "アップロード先のリモート場所を選択してください。"; return; }
        if (Local.Selected.Type != FileEntryTypes.File) { StatusMessage = "アップロードできるのはファイルだけです。"; return; }
        if (!Remote.SelectedLocation.Permissions.Write) { StatusMessage = "アップロード失敗: この場所には書き込み権限がありません。"; return; }
        var local = Path.Combine(Local.CurrentPath, Local.Selected.Name);
        var fi = new FileInfo(local);
        if (!fi.Exists) { StatusMessage = "アップロード失敗: ローカルファイルが見つかりません。"; return; }
        var remote = RemotePaneViewModel.JoinPath(Remote.CurrentPath, Local.Selected.Name);
        Transfer.FileName = Local.Selected.Name;
        Transfer.TotalBytes = fi.Length;
        Transfer.BytesTransferred = 0;
        Transfer.IsActive = true;
        try
        {
            await using var fs = File.OpenRead(local);
            var progress = new Progress<long>(b => Transfer.BytesTransferred = b);
            await _api.UploadAsync(Remote.SelectedLocation.HostId, Remote.SelectedLocation.ShareId, remote, fs, fi.Length, progress);
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
        if (Remote.Selected is null) { StatusMessage = "ダウンロードするリモートファイルを選択してください。"; return; }
        if (Remote.SelectedLocation is null) { StatusMessage = "ダウンロード元のリモート場所を選択してください。"; return; }
        if (Remote.Selected.Type != FileEntryTypes.File) { StatusMessage = "ダウンロードできるのはファイルだけです。"; return; }
        if (!Directory.Exists(Local.CurrentPath)) { StatusMessage = "ダウンロード失敗: ローカルフォルダが見つかりません。"; return; }

        var destination = Path.Combine(Local.CurrentPath, Remote.Selected.Name);
        if (File.Exists(destination))
        {
            var overwrite = MessageBox.Show(
                $"{Remote.Selected.Name} はローカルフォルダに既に存在します。上書きしますか？",
                "ダウンロード",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (overwrite != MessageBoxResult.Yes) return;
        }

        var remotePath = RemotePaneViewModel.JoinPath(Remote.CurrentPath, Remote.Selected.Name);
        Transfer.FileName = Remote.Selected.Name;
        Transfer.TotalBytes = Remote.Selected.Size ?? 0;
        Transfer.BytesTransferred = 0;
        Transfer.IsActive = true;
        // 失敗時に中途半端なファイルが残らないよう、まず .part に書き込み、完了時に rename する。
        var tempPath = destination + ".part";
        bool completed = false;
        try
        {
            var progress = new Progress<long>(b => Transfer.BytesTransferred = b);
            await using (var fs = File.Create(tempPath))
            {
                await _api.DownloadAsync(Remote.SelectedLocation.HostId, Remote.SelectedLocation.ShareId, remotePath, fs, progress);
                await fs.FlushAsync();
            }
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempPath, destination);
            completed = true;
            await Local.RefreshAsync();
            StatusMessage = $"ダウンロード完了: {Remote.Selected.Name}";
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "ダウンロード失敗: 読み取り権限がありません。";
        }
        catch (Exception ex) { StatusMessage = "ダウンロード失敗: " + ex.Message; }
        finally
        {
            Transfer.IsActive = false;
            if (!completed)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }
}
