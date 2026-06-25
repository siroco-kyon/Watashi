using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels.Admin;

public partial class SystemSettingsViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<ApiClient.SettingItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<AdminSortOption> SortOptions { get; } = new()
    {
        new("Key", nameof(ApiClient.SettingItem.Key)),
        new("Value", nameof(ApiClient.SettingItem.Value)),
        new("更新日時", nameof(ApiClient.SettingItem.UpdatedAt), ListSortDirection.Descending),
    };
    [ObservableProperty] private ApiClient.SettingItem? selected;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private AdminSortOption? selectedSortOption;
    [ObservableProperty] private string editValue = string.Empty;

    public SystemSettingsViewModel(ApiClient api)
    {
        _api = api;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is ApiClient.SettingItem s && MatchesSearch(SearchText, s.Key, s.Value, s.UpdatedAt);
        SelectedSortOption = SortOptions[0];
        ApplySort(ItemsView, SelectedSortOption);
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedSortOptionChanged(AdminSortOption? value) => ApplySort(ItemsView, value);

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedKey = Selected?.Key;
        ReplaceAll(Items, await _api.GetSettingsAsync());
        if (selectedKey is not null)
            Selected = Items.FirstOrDefault(i => i.Key == selectedKey);
    });

    partial void OnSelectedChanged(ApiClient.SettingItem? value) => EditValue = value?.Value ?? string.Empty;

    [RelayCommand]
    public Task SaveAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.PutSettingAsync(Selected.Key, EditValue);
        await RefreshAsync();
    }, successMessage: "保存しました。");
}
