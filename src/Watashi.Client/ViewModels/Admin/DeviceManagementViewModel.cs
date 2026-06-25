using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class DeviceManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<DeviceDto> Devices { get; } = new();
    public ICollectionView DevicesView { get; }
    public ObservableCollection<AdminSortOption> SortOptions { get; } = new()
    {
        new("ユーザー", nameof(DeviceDto.Username)),
        new("ID", nameof(DeviceDto.Id)),
        new("マシン", nameof(DeviceDto.MachineName)),
        new("Windowsユーザー", nameof(DeviceDto.WindowsUsername)),
        new("登録日時", nameof(DeviceDto.RegisteredAt), ListSortDirection.Descending),
        new("最終使用", nameof(DeviceDto.LastUsedAt), ListSortDirection.Descending),
        new("失効", nameof(DeviceDto.IsRevoked), ListSortDirection.Descending),
    };
    [ObservableProperty] private DeviceDto? selectedDevice;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private AdminSortOption? selectedSortOption;

    public bool HasSelectedDevice => SelectedDevice is not null;

    public DeviceManagementViewModel(ApiClient api)
    {
        _api = api;
        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = item => item is DeviceDto d && MatchesSearch(
            SearchText, d.Id, d.UserId, d.Username, d.MachineName, d.WindowsUsername,
            d.RegisteredAt, d.LastUsedAt, d.IsRevoked ? "失効 revoked" : "有効 active", d.RevokedReason);
        SelectedSortOption = SortOptions[0];
        ApplySort(DevicesView, SelectedSortOption);
    }

    partial void OnSelectedDeviceChanged(DeviceDto? value) => OnPropertyChanged(nameof(HasSelectedDevice));

    partial void OnSearchTextChanged(string value) => DevicesView.Refresh();

    partial void OnSelectedSortOptionChanged(AdminSortOption? value) => ApplySort(DevicesView, value);

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedId = SelectedDevice?.Id;
        ReplaceAll(Devices, await _api.GetAllDevicesAsync());
        if (selectedId.HasValue)
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId.Value);
    });

    [RelayCommand]
    public Task RevokeAllAsync() => SafeAsync(async () =>
    {
        if (SelectedDevice is null) { StatusMessage = "対象ユーザーのデバイス行を選択してください。"; return; }
        var username = string.IsNullOrWhiteSpace(SelectedDevice.Username) ? $"user#{SelectedDevice.UserId}" : SelectedDevice.Username;
        var confirm = System.Windows.MessageBox.Show(
            $"\"{username}\" の信頼デバイスを全て失効しますか？\n対象ユーザーは次回ログインで再度パスワード入力が必要になります。",
            "失効確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.RevokeDevicesAsync(SelectedDevice.UserId);
        await RefreshAsync();
    }, successMessage: "失効しました。");
}
