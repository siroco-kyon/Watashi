using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels.Admin;

public partial class SystemSettingsViewModel : ObservableObject
{
    private readonly ApiClient _api;
    public ObservableCollection<ApiClient.SettingItem> Items { get; } = new();
    [ObservableProperty] private ApiClient.SettingItem? selected;
    [ObservableProperty] private string editValue = string.Empty;
    [ObservableProperty] private string statusMessage = string.Empty;

    public SystemSettingsViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try { Items.Clear(); foreach (var s in await _api.GetSettingsAsync()) Items.Add(s); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    partial void OnSelectedChanged(ApiClient.SettingItem? value) => EditValue = value?.Value ?? string.Empty;

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Selected is null) return;
        try { await _api.PutSettingAsync(Selected.Key, EditValue); await RefreshAsync(); StatusMessage = "保存しました。"; }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
