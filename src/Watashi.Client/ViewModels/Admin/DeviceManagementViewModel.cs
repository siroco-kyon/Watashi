using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class DeviceManagementViewModel : ObservableObject
{
    private readonly ApiClient _api;
    public ObservableCollection<UserDto> Users { get; } = new();
    public ObservableCollection<DeviceDto> Devices { get; } = new();
    [ObservableProperty] private UserDto? selectedUser;
    [ObservableProperty] private string statusMessage = string.Empty;

    public DeviceManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try { Users.Clear(); foreach (var u in await _api.GetUsersAsync()) Users.Add(u); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    partial void OnSelectedUserChanged(UserDto? value) => _ = LoadDevicesAsync();

    private async Task LoadDevicesAsync()
    {
        Devices.Clear();
        if (SelectedUser is null) return;
        try { foreach (var d in await _api.GetDevicesAsync(SelectedUser.Id)) Devices.Add(d); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RevokeAllAsync()
    {
        if (SelectedUser is null) return;
        try { await _api.RevokeDevicesAsync(SelectedUser.Id); await LoadDevicesAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
