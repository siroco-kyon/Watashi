using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Client.ViewModels;

public partial class RemotePaneViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    public ObservableCollection<FileEntry> Entries { get; } = new();
    public ObservableCollection<LocationDto> Locations { get; } = new();

    [ObservableProperty] private LocationDto? selectedLocation;
    [ObservableProperty] private string currentPath = "/";
    [ObservableProperty] private FileEntry? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;
    [ObservableProperty] private bool canGoUp;

    public bool HasLocation => SelectedLocation is not null;
    public bool HasNoLocations => Locations.Count == 0;

    public RemotePaneViewModel(ApiClient api)
    {
        _api = api;
        Locations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoLocations));
    }

    partial void OnSelectedLocationChanged(LocationDto? value)
    {
        OnPropertyChanged(nameof(HasLocation));
        if (value is null)
        {
            Entries.Clear();
            CurrentPath = "/";
            CanGoUp = false;
            CanGoBack = false;
            CanGoForward = false;
            return;
        }
        _back.Clear();
        _forward.Clear();
        UpdateHistoryFlags();
        CurrentPath = value.Path;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task LoadHostsAndLocationsAsync()
    {
        try
        {
            IsBusy = true;
            var catalog = await _api.GetUserCatalogAsync();
            Locations.Clear();
            foreach (var h in catalog.Hosts)
                foreach (var s in h.Shares)
                    foreach (var l in s.Locations)
                        Locations.Add(l);
            if (Locations.Count > 0) SelectedLocation = Locations[0];
        }
        catch (Exception ex) { StatusMessage = "ロケーション取得失敗: " + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>任意パスへ移動（履歴に積む）。</summary>
    [RelayCommand]
    public async Task NavigateAsync(string? newPath)
    {
        if (SelectedLocation is null) return;
        var target = PathHelper.NormalizePath(newPath ?? CurrentPath);
        if (string.Equals(target, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshAsync();
            return;
        }
        if (!string.IsNullOrEmpty(CurrentPath)) _back.Push(CurrentPath);
        _forward.Clear();
        CurrentPath = target;
        UpdateHistoryFlags();
        await RefreshAsync();
    }

    [RelayCommand]
    public Task GoBackAsync()
    {
        if (_back.Count == 0) return Task.CompletedTask;
        var prev = _back.Pop();
        if (!string.IsNullOrEmpty(CurrentPath)) _forward.Push(CurrentPath);
        CurrentPath = prev;
        UpdateHistoryFlags();
        return RefreshAsync();
    }

    [RelayCommand]
    public Task GoForwardAsync()
    {
        if (_forward.Count == 0) return Task.CompletedTask;
        var next = _forward.Pop();
        if (!string.IsNullOrEmpty(CurrentPath)) _back.Push(CurrentPath);
        CurrentPath = next;
        UpdateHistoryFlags();
        return RefreshAsync();
    }

    [RelayCommand]
    public Task GoUpAsync()
    {
        if (!CanGoUp) return Task.CompletedTask;
        var parent = PathHelper.GetParent(CurrentPath);
        if (parent == CurrentPath) return Task.CompletedTask;
        return NavigateAsync(parent);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (SelectedLocation is null) return;
        try
        {
            IsBusy = true;
            var res = await _api.ListFilesAsync(SelectedLocation.HostId, SelectedLocation.ShareId, CurrentPath);
            Entries.Clear();
            foreach (var e in res.Entries) Entries.Add(e);
            // 親へ戻れるかはサーバが parent entry の CanGoUp で示すので、それを採用。
            CanGoUp = res.Entries.FirstOrDefault(x => x.Type == FileEntryTypes.Parent)?.CanGoUp == true;
            StatusMessage = string.Empty;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task OpenSelectedAsync()
    {
        if (Selected is null || SelectedLocation is null) return;
        if (Selected.Type == FileEntryTypes.Parent)
        {
            if (Selected.CanGoUp != true) return;
            await GoUpAsync();
            return;
        }
        if (Selected.Type == FileEntryTypes.Directory)
        {
            await NavigateAsync(JoinPath(CurrentPath, Selected.Name));
        }
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (Selected is null || SelectedLocation is null || Selected.Type == FileEntryTypes.Parent) return;
        if (!SelectedLocation.Permissions.Delete)
        {
            StatusMessage = "削除失敗: 削除権限がありません。";
            return;
        }
        var name = Selected.Name;
        try
        {
            await _api.DeleteFileAsync(SelectedLocation.HostId, SelectedLocation.ShareId, JoinPath(CurrentPath, name));
            await RefreshAsync();
            StatusMessage = $"削除しました: {name}";
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "削除失敗: " + ex.Message;
        }
        catch (Exception ex) { StatusMessage = "削除失敗: " + ex.Message; }
    }

    [RelayCommand]
    public async Task NewFolderAsync(string? name)
    {
        if (SelectedLocation is null || string.IsNullOrWhiteSpace(name)) return;
        if (!SelectedLocation.Permissions.Write)
        {
            StatusMessage = "フォルダ作成失敗: 書き込み権限がありません。";
            return;
        }
        try
        {
            await _api.MkdirAsync(SelectedLocation.HostId, SelectedLocation.ShareId, JoinPath(CurrentPath, name));
            await RefreshAsync();
            StatusMessage = $"フォルダを作成しました: {name}";
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "フォルダ作成失敗: " + ex.Message;
        }
        catch (Exception ex) { StatusMessage = "フォルダ作成失敗: " + ex.Message; }
    }

    /// <summary>
    /// 選択中アイテムを同フォルダ内でリネーム。サーバ側で「親ディレクトリの変更不可」「許可ルートそのものは
    /// リネーム不可」のチェックが入っているので、それらは StatusMessage に伝播するだけ。
    /// </summary>
    public async Task RenameSelectedAsync(string? newName)
    {
        if (Selected is null || SelectedLocation is null || Selected.Type == FileEntryTypes.Parent) return;
        if (string.IsNullOrWhiteSpace(newName) || newName == Selected.Name) return;
        if (!SelectedLocation.Permissions.Rename)
        {
            StatusMessage = "リネーム失敗: リネーム権限がありません。";
            return;
        }
        // 「/」「\」を含む名前は親ディレクトリ変更とみなされサーバが拒否する。クライアント側で先弾き。
        if (newName.Contains('/') || newName.Contains('\\'))
        {
            StatusMessage = "リネーム名に / や \\ は含められません";
            return;
        }
        var oldPath = JoinPath(CurrentPath, Selected.Name);
        var newPath = JoinPath(CurrentPath, newName);
        try
        {
            await _api.RenameAsync(SelectedLocation.HostId, SelectedLocation.ShareId, oldPath, newPath);
            await RefreshAsync();
            StatusMessage = $"リネーム: → {newName}";
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "リネーム失敗: " + ex.Message;
        }
        catch (Exception ex) { StatusMessage = "リネーム失敗: " + ex.Message; }
    }

    /// <summary>
    /// 選択中アイテム (なければ CurrentPath) のリモートパスをクリップボードへコピー。
    /// 共有ルートを含む形 (例: 経理部FS / share-keiri :: /dept-A/file.txt) ではなく
    /// パス部分のみ。FTPツール等への貼り付け用途。
    /// </summary>
    [RelayCommand]
    public void CopyPath()
    {
        try
        {
            var path = Selected is not null && Selected.Type != FileEntryTypes.Parent
                ? JoinPath(CurrentPath, Selected.Name)
                : CurrentPath;
            System.Windows.Clipboard.SetText(path);
            StatusMessage = $"パスをコピー: {path}";
        }
        catch (Exception ex) { StatusMessage = "クリップボードへコピー失敗: " + ex.Message; }
    }

    private void UpdateHistoryFlags()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
    }

    public static string JoinPath(string parent, string name)
    {
        var p = parent.TrimEnd('/');
        return p == string.Empty ? "/" + name : p + "/" + name;
    }
}
