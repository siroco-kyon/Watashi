using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class NodeManagementViewModel : ObservableObject
{
    private readonly ApiClient _api;
    public ObservableCollection<NodeDto> Items { get; } = new();
    [ObservableProperty] private NodeDto? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string newName = string.Empty;
    [ObservableProperty] private string newType = NodeTypes.Direct;
    [ObservableProperty] private string? newEndpoint;
    [ObservableProperty] private int newMaxConcurrency = 20;

    public NodeManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try { Items.Clear(); foreach (var n in await _api.GetNodesAsync()) Items.Add(n); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        try
        {
            await _api.CreateNodeAsync(new CreateNodeRequest
            {
                Name = NewName, NodeType = NewType, Endpoint = NewEndpoint, MaxConcurrency = NewMaxConcurrency,
            });
            NewName = string.Empty; NewEndpoint = null;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _api.DeleteNodeAsync(Selected.Id); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
