using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels.Admin;

public partial class SystemSettingsViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<ApiClient.SettingItem> Items { get; } = new();
    [ObservableProperty] private ApiClient.SettingItem? selected;
    [ObservableProperty] private string editValue = string.Empty;

    public SystemSettingsViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () => ReplaceAll(Items, await _api.GetSettingsAsync()));

    partial void OnSelectedChanged(ApiClient.SettingItem? value) => EditValue = value?.Value ?? string.Empty;

    [RelayCommand]
    public Task SaveAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.PutSettingAsync(Selected.Key, EditValue);
        await RefreshAsync();
    }, successMessage: "保存しました。");
}
