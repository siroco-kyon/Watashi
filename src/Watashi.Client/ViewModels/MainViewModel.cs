using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Watashi.Client.Services;

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
    public async Task UploadAsync()
    {
        if (Local.Selected is null || Remote.SelectedLocation is null) return;
        if (Local.Selected.Type != Watashi.Shared.Constants.FileEntryTypes.File) return;
        var local = Path.Combine(Local.CurrentPath, Local.Selected.Name);
        var fi = new FileInfo(local);
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
        catch (Exception ex) { StatusMessage = "アップロード失敗: " + ex.Message; }
        finally { Transfer.IsActive = false; }
    }

    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (Remote.Selected is null || Remote.SelectedLocation is null) return;
        if (Remote.Selected.Type != Watashi.Shared.Constants.FileEntryTypes.File) return;
        var dlg = new SaveFileDialog
        {
            FileName = Remote.Selected.Name,
            InitialDirectory = Local.CurrentPath,
        };
        if (dlg.ShowDialog() != true) return;
        var remotePath = RemotePaneViewModel.JoinPath(Remote.CurrentPath, Remote.Selected.Name);
        Transfer.FileName = Remote.Selected.Name;
        Transfer.TotalBytes = Remote.Selected.Size ?? 0;
        Transfer.BytesTransferred = 0;
        Transfer.IsActive = true;
        try
        {
            await using var fs = File.Create(dlg.FileName);
            var progress = new Progress<long>(b => Transfer.BytesTransferred = b);
            await _api.DownloadAsync(Remote.SelectedLocation.HostId, Remote.SelectedLocation.ShareId, remotePath, fs, progress);
            await Local.RefreshAsync();
            StatusMessage = $"ダウンロード完了: {Remote.Selected.Name}";
        }
        catch (Exception ex) { StatusMessage = "ダウンロード失敗: " + ex.Message; }
        finally { Transfer.IsActive = false; }
    }
}
