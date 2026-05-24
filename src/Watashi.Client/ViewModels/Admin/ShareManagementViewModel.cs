using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class ShareManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<ShareDto> Items { get; } = new();
    public ObservableCollection<HostDto> Hosts { get; } = new();
    [ObservableProperty] private ShareDto? selected;
    [ObservableProperty] private int? newHostId;
    [ObservableProperty] private string newShareName = string.Empty;
    [ObservableProperty] private string newDisplayName = string.Empty;

    public ShareManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var sharesTask = _api.GetAdminSharesAsync();
        var hostsTask = _api.GetAdminHostsAsync();
        await Task.WhenAll(sharesTask, hostsTask);
        ReplaceAll(Items, sharesTask.Result);
        ReplaceAll(Hosts, hostsTask.Result);
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
