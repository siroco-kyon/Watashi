using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class HostManagementViewModel : ObservableObject
{
    private readonly ApiClient _api;
    public ObservableCollection<HostDto> Items { get; } = new();
    public ObservableCollection<NodeDto> Nodes { get; } = new();
    [ObservableProperty] private HostDto? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string hostAddress = string.Empty;
    [ObservableProperty] private int port = 445;
    [ObservableProperty] private string credUser = string.Empty;
    [ObservableProperty] private string credPassword = string.Empty;
    [ObservableProperty] private NodeDto? selectedNode;

    public HostManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            Items.Clear();
            foreach (var h in await _api.GetAdminHostsAsync()) Items.Add(h);
            Nodes.Clear();
            foreach (var n in await _api.GetNodesAsync()) Nodes.Add(n);
            SelectedNode ??= Nodes.FirstOrDefault();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        if (SelectedNode is null) { StatusMessage = "ノードを選択してください。"; return; }
        try
        {
            await _api.CreateHostAsync(new CreateHostRequest
            {
                Name = Name, HostAddress = HostAddress, Port = Port,
                CredUsername = CredUser, CredPassword = CredPassword,
                ExecutionNodeId = SelectedNode.Id,
            });
            Name = HostAddress = CredUser = CredPassword = string.Empty;
            Port = 445;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _api.DeleteHostAsync(Selected.Id); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task TestAsync()
    {
        if (Selected is null) return;
        try { var ok = await _api.TestHostAsync(Selected.Id); StatusMessage = ok ? "✓ 接続OK" : "✗ 接続失敗"; }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
