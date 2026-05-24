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
        if (string.IsNullOrWhiteSpace(NewName)) { StatusMessage = "ノード名を入力してください。"; return; }
        if (NewType == NodeTypes.Agent && string.IsNullOrWhiteSpace(NewEndpoint))
        { StatusMessage = "Agent タイプでは Endpoint を入力してください。"; return; }
        if (NewMaxConcurrency < 1) { StatusMessage = "MaxConcurrency は 1 以上で指定してください。"; return; }
        await _api.CreateNodeAsync(new CreateNodeRequest
        {
            Name = NewName, NodeType = NewType, Endpoint = NewEndpoint, MaxConcurrency = NewMaxConcurrency,
        });
        NewName = string.Empty; NewEndpoint = null;
        await RefreshAsync();
    }, successMessage: "ノードを作成しました。");

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
