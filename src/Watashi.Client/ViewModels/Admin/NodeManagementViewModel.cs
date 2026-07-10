using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class NodeManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<NodeDto> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<AdminSortOption> SortOptions { get; } = new()
    {
        new("名前", nameof(NodeDto.Name)),
        new("ID", nameof(NodeDto.Id)),
        new("種別", nameof(NodeDto.NodeType)),
        new("Health", nameof(NodeDto.HealthStatus)),
        new("LastBeat", nameof(NodeDto.LastHeartbeatAt), ListSortDirection.Descending),
        new("作成日時", nameof(NodeDto.CreatedAt), ListSortDirection.Descending),
    };
    [ObservableProperty] private NodeDto? selected;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private AdminSortOption? selectedSortOption;
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

    public NodeManagementViewModel(ApiClient api)
    {
        _api = api;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is NodeDto n && MatchesSearch(
            SearchText, n.Id, n.Name, n.NodeType, n.Endpoint, n.GatewayNodeName, n.HealthStatus,
            n.IsActive ? "有効 active" : "無効 inactive", n.MaxConcurrency);
        SelectedSortOption = SortOptions[0];
        ApplySort(ItemsView, SelectedSortOption);
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedSortOptionChanged(AdminSortOption? value) => ApplySort(ItemsView, value);

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
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedId = Selected?.Id;
        var nodesTask = _api.GetNodesAsync();
        var settingsTask = _api.GetSettingsAsync();
        await Task.WhenAll(nodesTask, settingsTask);
        ReplaceAll(Items, await nodesTask);
        var configuredDefault = (await settingsTask)
            .FirstOrDefault(s => s.Key == SettingKeys.AgentMaxConcurrency)?.Value;
        if (int.TryParse(configuredDefault, out var defaultConcurrency) && defaultConcurrency is >= 1 and <= 100_000)
            NewMaxConcurrency = defaultConcurrency;
        if (selectedId.HasValue)
            Selected = Items.FirstOrDefault(n => n.Id == selectedId.Value);
    });

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewName)) { StatusMessage = "ノード名を入力してください。"; return; }
        if (NewType == NodeTypes.Agent && string.IsNullOrWhiteSpace(NewEndpoint))
        { StatusMessage = "Agent タイプでは Endpoint を入力してください。"; return; }
        if (NewUseGateway && NewGatewayNodeId is null)
        { StatusMessage = "経由 Agent を選択してください。"; return; }
        if (NewMaxConcurrency is < 1 or > 100_000) { StatusMessage = "MaxConcurrency は 1 以上 100000 以下で指定してください。"; return; }
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
        if (EditMaxConcurrency is < 1 or > 100_000) { StatusMessage = "MaxConcurrency は 1 以上 100000 以下で指定してください。"; return; }

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
