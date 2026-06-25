using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class ShareManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<ShareDto> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<HostDto> Hosts { get; } = new();
    public ObservableCollection<AdminSortOption> SortOptions { get; } = new()
    {
        new("表示名", nameof(ShareDto.DisplayName)),
        new("ID", nameof(ShareDto.Id)),
        new("ホスト", nameof(ShareDto.HostName)),
        new("共有名", nameof(ShareDto.ShareName)),
    };
    [ObservableProperty] private ShareDto? selected;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private AdminSortOption? selectedSortOption;
    [ObservableProperty] private int? newHostId;
    [ObservableProperty] private string newShareName = string.Empty;
    [ObservableProperty] private string newDisplayName = string.Empty;
    [ObservableProperty] private int? editHostId;
    [ObservableProperty] private string editShareName = string.Empty;
    [ObservableProperty] private string editDisplayName = string.Empty;

    public bool HasSelected => Selected is not null;

    public ShareManagementViewModel(ApiClient api)
    {
        _api = api;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is ShareDto s && MatchesSearch(
            SearchText, s.Id, s.HostId, s.HostName, s.ShareName, s.DisplayName);
        SelectedSortOption = SortOptions[0];
        ApplySort(ItemsView, SelectedSortOption);
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedSortOptionChanged(AdminSortOption? value) => ApplySort(ItemsView, value);

    partial void OnSelectedChanged(ShareDto? value)
    {
        OnPropertyChanged(nameof(HasSelected));
        EditHostId = value?.HostId;
        EditShareName = value?.ShareName ?? string.Empty;
        EditDisplayName = value?.DisplayName ?? string.Empty;
    }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedId = Selected?.Id;
        var sharesTask = _api.GetAdminSharesAsync();
        var hostsTask = _api.GetAdminHostsAsync();
        await Task.WhenAll(sharesTask, hostsTask);
        ReplaceAll(Items, sharesTask.Result);
        ReplaceAll(Hosts, hostsTask.Result);
        if (selectedId.HasValue)
            Selected = Items.FirstOrDefault(s => s.Id == selectedId.Value);
        if (NewHostId is null || Hosts.All(h => h.Id != NewHostId.Value))
            NewHostId = Hosts.FirstOrDefault()?.Id;
    });

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (NewHostId is null) { StatusMessage = "対象ホストを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(NewShareName)) { StatusMessage = "共有名 (SMB) を入力してください。"; return; }
        if (string.IsNullOrWhiteSpace(NewDisplayName)) { StatusMessage = "表示名を入力してください。"; return; }
        await _api.CreateShareAsync(new CreateShareRequest { HostId = NewHostId.Value, ShareName = NewShareName, DisplayName = NewDisplayName });
        NewShareName = NewDisplayName = string.Empty;
        await RefreshAsync();
    }, successMessage: "共有を作成しました。");

    [RelayCommand]
    public Task SaveAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "保存する共有を選択してください。"; return; }
        if (EditHostId is null) { StatusMessage = "対象ホストを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(EditShareName)) { StatusMessage = "共有名 (SMB) を入力してください。"; return; }
        if (string.IsNullOrWhiteSpace(EditDisplayName)) { StatusMessage = "表示名を入力してください。"; return; }
        await _api.UpdateShareAsync(Selected.Id, new UpdateShareRequest
        {
            HostId = EditHostId.Value,
            ShareName = EditShareName,
            DisplayName = EditDisplayName,
        });
        await RefreshAsync();
    }, successMessage: "保存しました。");

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除する共有を選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"共有 \"{Selected.DisplayName}\" を削除しますか？\nこの共有に紐づく権限も削除されます。",
            "削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteShareAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "削除しました。");
}
