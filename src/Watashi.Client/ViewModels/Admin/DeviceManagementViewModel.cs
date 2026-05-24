using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class DeviceManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<UserDto> Users { get; } = new();
    public ObservableCollection<DeviceDto> Devices { get; } = new();
    [ObservableProperty] private UserDto? selectedUser;

    public DeviceManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () => ReplaceAll(Users, await _api.GetUsersAsync()));

    partial void OnSelectedUserChanged(UserDto? value) => _ = LoadDevicesAsync();

    private Task LoadDevicesAsync() => SafeAsync(async () =>
    {
        Devices.Clear();
        if (SelectedUser is null) return;
        ReplaceAll(Devices, await _api.GetDevicesAsync(SelectedUser.Id));
    });

    [RelayCommand]
    public Task RevokeAllAsync() => SafeAsync(async () =>
    {
        if (SelectedUser is null) { StatusMessage = "対象ユーザーを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"\"{SelectedUser.Username}\" の信頼デバイスを全て失効しますか？\n対象ユーザーは次回ログインで再度パスワード入力が必要になります。",
            "失効確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.RevokeDevicesAsync(SelectedUser.Id);
        await LoadDevicesAsync();
    }, successMessage: "失効しました。");
}
