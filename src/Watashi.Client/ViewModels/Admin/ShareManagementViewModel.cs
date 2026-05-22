using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class ShareManagementViewModel : ObservableObject
{
    private readonly ApiClient _api;
    public ObservableCollection<ShareDto> Items { get; } = new();
    public ObservableCollection<HostDto> Hosts { get; } = new();
    [ObservableProperty] private ShareDto? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private HostDto? newHost;
    [ObservableProperty] private string newShareName = string.Empty;
    [ObservableProperty] private string newDisplayName = string.Empty;

    public ShareManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            Items.Clear();
            foreach (var s in await _api.GetAdminSharesAsync()) Items.Add(s);
            Hosts.Clear();
            foreach (var h in await _api.GetAdminHostsAsync()) Hosts.Add(h);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        if (NewHost is null) { StatusMessage = "ホストを選んでください。"; return; }
        try
        {
            await _api.CreateShareAsync(new CreateShareRequest { HostId = NewHost.Id, ShareName = NewShareName, DisplayName = NewDisplayName });
            NewShareName = NewDisplayName = string.Empty;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _api.DeleteShareAsync(Selected.Id); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
