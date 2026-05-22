using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class HostManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<HostDto> Items { get; } = new();
    public ObservableCollection<NodeDto> Nodes { get; } = new();
    [ObservableProperty] private HostDto? selected;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string hostAddress = string.Empty;
    [ObservableProperty] private int port = 445;
    [ObservableProperty] private string credUser = string.Empty;
    [ObservableProperty] private string credPassword = string.Empty;
    [ObservableProperty] private NodeDto? selectedNode;

    public HostManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var hostsTask = _api.GetAdminHostsAsync();
        var nodesTask = _api.GetNodesAsync();
        await Task.WhenAll(hostsTask, nodesTask);
        ReplaceAll(Items, hostsTask.Result);
        ReplaceAll(Nodes, nodesTask.Result);
        SelectedNode ??= Nodes.FirstOrDefault();
    });

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (SelectedNode is null) { StatusMessage = "ノードを選択してください。"; return; }
        await _api.CreateHostAsync(new CreateHostRequest
        {
            Name = Name, HostAddress = HostAddress, Port = Port,
            CredUsername = CredUser, CredPassword = CredPassword,
            ExecutionNodeId = SelectedNode.Id,
        });
        Name = HostAddress = CredUser = CredPassword = string.Empty;
        Port = 445;
        await RefreshAsync();
    });

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.DeleteHostAsync(Selected.Id);
        await RefreshAsync();
    });

    [RelayCommand]
    public Task TestAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        var ok = await _api.TestHostAsync(Selected.Id);
        StatusMessage = ok ? "✓ 接続OK" : "✗ 接続失敗";
    });
}
