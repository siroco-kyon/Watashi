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
    [ObservableProperty] private int? selectedNodeId;

    public HostManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var hostsTask = _api.GetAdminHostsAsync();
        var nodesTask = _api.GetNodesAsync();
        await Task.WhenAll(hostsTask, nodesTask);
        ReplaceAll(Items, hostsTask.Result);
        ReplaceAll(Nodes, nodesTask.Result);
        if (Selected is not null)
        {
            var refreshed = Items.FirstOrDefault(h => h.Id == Selected.Id);
            if (refreshed is not null)
                Selected = refreshed;
            else
                ClearForm();
        }
        else if (SelectedNodeId is null || Nodes.All(n => n.Id != SelectedNodeId.Value))
        {
            SelectedNodeId = Nodes.FirstOrDefault()?.Id;
        }
    });

    partial void OnSelectedChanged(HostDto? value)
    {
        if (value is null) return;
        Name = value.Name;
        HostAddress = value.HostAddress;
        Port = value.Port;
        CredUser = value.CredUsername;
        CredPassword = string.Empty;
        SelectedNodeId = value.ExecutionNodeId;
    }

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Name)) { StatusMessage = "表示名を入力してください。"; return; }
        if (string.IsNullOrWhiteSpace(HostAddress)) { StatusMessage = "ホスト名/IP を入力してください。"; return; }
        if (Port <= 0 || Port > 65535) { StatusMessage = "ポートは 1〜65535 で指定してください。"; return; }
        if (string.IsNullOrWhiteSpace(CredUser)) { StatusMessage = "CIFS ユーザーを入力してください。"; return; }
        if (string.IsNullOrEmpty(CredPassword)) { StatusMessage = "新規作成では CIFS パスワードを入力してください。"; return; }
        if (SelectedNodeId is null) { StatusMessage = "実行ノードを選択してください。"; return; }
        await _api.CreateHostAsync(new CreateHostRequest
        {
            Name = Name, HostAddress = HostAddress, Port = Port,
            CredUsername = CredUser, CredPassword = CredPassword,
            ExecutionNodeId = SelectedNodeId.Value,
        });
        ClearForm();
        await RefreshAsync();
    }, successMessage: "ホストを作成しました。");

    [RelayCommand]
    public Task SaveAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "保存するホストを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(Name)) { StatusMessage = "表示名を入力してください。"; return; }
        if (string.IsNullOrWhiteSpace(HostAddress)) { StatusMessage = "ホスト名/IP を入力してください。"; return; }
        if (Port <= 0 || Port > 65535) { StatusMessage = "ポートは 1〜65535 で指定してください。"; return; }
        if (string.IsNullOrWhiteSpace(CredUser)) { StatusMessage = "CIFS ユーザーを入力してください。"; return; }
        if (SelectedNodeId is null) { StatusMessage = "実行ノードを選択してください。"; return; }
        await _api.UpdateHostAsync(Selected.Id, new UpdateHostRequest
        {
            Name = Name,
            HostAddress = HostAddress,
            Port = Port,
            CredUsername = CredUser,
            CredPassword = string.IsNullOrEmpty(CredPassword) ? null : CredPassword,
            ExecutionNodeId = SelectedNodeId.Value,
        });
        await RefreshAsync();
    }, successMessage: "保存しました。");

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除するホストを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"ホスト \"{Selected.Name}\" を削除しますか？\nこのホストに紐づく共有・権限・履歴も削除されます。",
            "削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteHostAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "削除しました。");

    [RelayCommand]
    public Task TestAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "テスト対象のホストを選択してください。"; return; }
        var ok = await _api.TestHostAsync(Selected.Id);
        StatusMessage = ok ? "✓ 接続OK" : "✗ 接続失敗";
    });

    [RelayCommand]
    public void ClearForm()
    {
        Selected = null;
        Name = HostAddress = CredUser = CredPassword = string.Empty;
        Port = 445;
        SelectedNodeId = Nodes.FirstOrDefault()?.Id;
    }
}
