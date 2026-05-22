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
    public ObservableCollection<FileEntry> Entries { get; } = new();
    public ObservableCollection<LocationDto> Locations { get; } = new();

    [ObservableProperty] private LocationDto? selectedLocation;
    [ObservableProperty] private string currentPath = "/";
    [ObservableProperty] private FileEntry? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    public RemotePaneViewModel(ApiClient api) { _api = api; }

    partial void OnSelectedLocationChanged(LocationDto? value)
    {
        if (value is null) return;
        CurrentPath = value.Path;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task LoadHostsAndLocationsAsync()
    {
        try
        {
            IsBusy = true;
            // catalog エンドポイントで host/share/location を 1 リクエスト集約取得。
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
            CurrentPath = PathHelper.GetParent(CurrentPath);
            await RefreshAsync();
            return;
        }
        if (Selected.Type == FileEntryTypes.Directory)
        {
            CurrentPath = JoinPath(CurrentPath, Selected.Name);
            await RefreshAsync();
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

    public static string JoinPath(string parent, string name)
    {
        var p = parent.TrimEnd('/');
        return p == string.Empty ? "/" + name : p + "/" + name;
    }
}
