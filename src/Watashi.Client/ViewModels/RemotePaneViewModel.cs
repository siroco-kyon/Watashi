using System.Collections.ObjectModel;
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

    public RemotePaneViewModel(ApiClient api) { _api = api; }

    partial void OnSelectedLocationChanged(LocationDto? value)
    {
        if (value is null) return;
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
        try
        {
            await _api.DeleteFileAsync(SelectedLocation.HostId, SelectedLocation.ShareId, JoinPath(CurrentPath, Selected.Name));
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task NewFolderAsync(string? name)
    {
        if (SelectedLocation is null || string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _api.MkdirAsync(SelectedLocation.HostId, SelectedLocation.ShareId, JoinPath(CurrentPath, name));
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
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
