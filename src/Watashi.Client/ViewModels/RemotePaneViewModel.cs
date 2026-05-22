using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
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
            Locations.Clear();
            var hosts = await _api.GetHostsAsync();
            foreach (var h in hosts)
            {
                var shares = await _api.GetSharesAsync(h.Id);
                foreach (var s in shares)
                {
                    var locs = await _api.GetLocationsAsync(h.Id, s.Id);
                    foreach (var l in locs) Locations.Add(l);
                }
            }
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
            Entries.Clear();
            var res = await _api.ListFilesAsync(SelectedLocation.HostId, SelectedLocation.ShareId, CurrentPath);
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
        if (Selected.Type == "parent")
        {
            if (Selected.CanGoUp != true) return;
            CurrentPath = PathHelper.GetParent(CurrentPath);
            await RefreshAsync();
            return;
        }
        if (Selected.Type == "directory")
        {
            CurrentPath = JoinPath(CurrentPath, Selected.Name);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (Selected is null || SelectedLocation is null || Selected.Type == "parent") return;
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
