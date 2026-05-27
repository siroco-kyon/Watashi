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
    [ObservableProperty] private bool newUseGateway;
    [ObservableProperty] private int? newGatewayNodeId;
    [ObservableProperty] private int newMaxConcurrency = 20;
    [ObservableProperty] private string editName = string.Empty;
    [ObservableProperty] private string? editEndpoint;
    [ObservableProperty] private bool editIsActive = true;
    [ObservableProperty] private bool editUseGateway;
    [ObservableProperty] private int? editGatewayNodeId;
    [ObservableProperty] private int editMaxConcurrency = 20;

    public NodeManagementViewModel(ApiClient api) { _api = api; }

    partial void OnSelectedChanged(NodeDto? value)
    {
        if (value is null)
        {
            EditName = string.Empty;
            EditEndpoint = null;
            EditIsActive = true;
            EditUseGateway = false;
            EditGatewayNodeId = null;
            EditMaxConcurrency = 20;
            return;
        }

        EditName = value.Name;
        EditEndpoint = value.Endpoint;
        EditIsActive = value.IsActive;
        EditUseGateway = value.GatewayNodeId.HasValue;
        EditGatewayNodeId = value.GatewayNodeId;
        EditMaxConcurrency = value.MaxConcurrency;
    }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () => ReplaceAll(Items, await _api.GetNodesAsync()));

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewName)) { StatusMessage = "ノード名を入力してください。"; return; }
        if (NewType == NodeTypes.Agent && string.IsNullOrWhiteSpace(NewEndpoint))
        { StatusMessage = "Agent タイプでは Endpoint を入力してください。"; return; }
        if (NewUseGateway && NewGatewayNodeId is null)
        { StatusMessage = "経由 Agent を選択してください。"; return; }
        if (NewMaxConcurrency < 1) { StatusMessage = "MaxConcurrency は 1 以上で指定してください。"; return; }
        await _api.CreateNodeAsync(new CreateNodeRequest
        {
            Name = NewName,
            NodeType = NewType,
            Endpoint = NewEndpoint,
            GatewayNodeId = NewUseGateway ? NewGatewayNodeId : null,
            MaxConcurrency = NewMaxConcurrency,
        });
        NewName = string.Empty; NewEndpoint = null; NewUseGateway = false; NewGatewayNodeId = null;
        await RefreshAsync();
    }, successMessage: "ノードを作成しました。");

    [RelayCommand]
    public Task UpdateAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "更新するノードを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(EditName)) { StatusMessage = "ノード名を入力してください。"; return; }
        if (Selected.NodeType == NodeTypes.Agent && string.IsNullOrWhiteSpace(EditEndpoint))
        { StatusMessage = "Agent タイプでは Endpoint を入力してください。"; return; }
        if (EditUseGateway && EditGatewayNodeId is null)
        { StatusMessage = "経由 Agent を選択してください。"; return; }
        if (EditMaxConcurrency < 1) { StatusMessage = "MaxConcurrency は 1 以上で指定してください。"; return; }

        await _api.UpdateNodeAsync(Selected.Id, new UpdateNodeRequest
        {
            Name = EditName,
            Endpoint = EditEndpoint,
            IsActive = EditIsActive,
            GatewayNodeId = EditUseGateway ? EditGatewayNodeId : null,
            ClearGatewayNode = !EditUseGateway,
            MaxConcurrency = EditMaxConcurrency,
        });
        await RefreshAsync();
    }, successMessage: "ノードを更新しました。");

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除するノードを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"ノード \"{Selected.Name}\" を削除しますか？\n使用中のホストがある場合は削除できません。",
            "削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteNodeAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "削除しました。");
}
