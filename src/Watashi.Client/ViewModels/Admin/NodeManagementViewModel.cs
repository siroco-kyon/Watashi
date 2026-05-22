using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class NodeManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<NodeDto> Items { get; } = new();
    [ObservableProperty] private NodeDto? selected;
    [ObservableProperty] private string newName = string.Empty;
    [ObservableProperty] private string newType = NodeTypes.Direct;
    [ObservableProperty] private string? newEndpoint;
    [ObservableProperty] private int newMaxConcurrency = 20;

    public NodeManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () => ReplaceAll(Items, await _api.GetNodesAsync()));

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        await _api.CreateNodeAsync(new CreateNodeRequest
        {
            Name = NewName, NodeType = NewType, Endpoint = NewEndpoint, MaxConcurrency = NewMaxConcurrency,
        });
        NewName = string.Empty; NewEndpoint = null;
        await RefreshAsync();
    });

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.DeleteNodeAsync(Selected.Id);
        await RefreshAsync();
    });
}
