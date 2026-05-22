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
    [ObservableProperty] private HostDto? newHost;
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
    });

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (NewHost is null) { StatusMessage = "ホストを選んでください。"; return; }
        await _api.CreateShareAsync(new CreateShareRequest { HostId = NewHost.Id, ShareName = NewShareName, DisplayName = NewDisplayName });
        NewShareName = NewDisplayName = string.Empty;
        await RefreshAsync();
    });

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.DeleteShareAsync(Selected.Id);
        await RefreshAsync();
    });
}
