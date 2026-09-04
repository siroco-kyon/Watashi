using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class ShareManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<ShareDto> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<HostDto> Hosts { get; } = new();
    public ObservableCollection<AdminSortOption> SortOptions { get; } = new()
    {
        new("表示名", nameof(ShareDto.DisplayName)),
        new("ID", nameof(ShareDto.Id)),
        new("ホスト", nameof(ShareDto.HostName)),
        new("共有名", nameof(ShareDto.ShareName)),
    };
    [ObservableProperty] private ShareDto? selected;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private AdminSortOption? selectedSortOption;
    [ObservableProperty] private int? newHostId;
    [ObservableProperty] private string newShareName = string.Empty;
    [ObservableProperty] private string newDisplayName = string.Empty;
    [ObservableProperty] private int? editHostId;
    [ObservableProperty] private string editShareName = string.Empty;
    [ObservableProperty] private string editDisplayName = string.Empty;

    public bool HasSelected => Selected is not null;

    /// <summary>選択中の共有に残る未完了の転送/ごみ箱データ。接続先変更・削除をブロックする。</summary>
    public ObservableCollection<string> DurableStateLines { get; } = new();
    [ObservableProperty] private string durableStateSummary = string.Empty;
    [ObservableProperty] private bool hasDurableState;

    public ShareManagementViewModel(ApiClient api)
    {
        _api = api;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is ShareDto s && MatchesSearch(
            SearchText, s.Id, s.HostId, s.HostName, s.ShareName, s.DisplayName);
        SelectedSortOption = SortOptions[0];
        ApplySort(ItemsView, SelectedSortOption);
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedSortOptionChanged(AdminSortOption? value) => ApplySort(ItemsView, value);

    partial void OnSelectedChanged(ShareDto? value)
    {
        OnPropertyChanged(nameof(HasSelected));
        EditHostId = value?.HostId;
        EditShareName = value?.ShareName ?? string.Empty;
        EditDisplayName = value?.DisplayName ?? string.Empty;
        _ = LoadDurableStateAsync(value?.Id);
    }

    /// <summary>
    /// 共有がブロックされている理由を一覧に出す。ここが空でないと接続先変更・削除が 409 になるため、
    /// 「待てば消えるのか、詰まっていて強制解除が要るのか」を管理者が判断できるようにする。
    /// </summary>
    private async Task LoadDurableStateAsync(int? shareId)
    {
        DurableStateLines.Clear();
        DurableStateSummary = string.Empty;
        HasDurableState = false;
        if (shareId is null) return;
        try
        {
            var state = await _api.GetShareDurableStateAsync(shareId.Value);
            if (Selected?.Id != shareId) return;
            foreach (var u in state.Uploads)
                DurableStateLines.Add($"転送: {u.TargetPath} [{u.Status}{FormatErrorCode(u.ErrorCode)}] 期限 {u.ExpiresAt.ToLocalTime():yyyy/MM/dd HH:mm}");
            foreach (var t in state.Trash)
                DurableStateLines.Add($"ごみ箱: {t.OriginalPath} [{t.Status}{FormatErrorCode(t.ErrorCode)}] 期限 {t.ExpiresAt.ToLocalTime():yyyy/MM/dd HH:mm}");
            HasDurableState = state.BlocksPhysicalChange;
            DurableStateSummary = state.BlocksPhysicalChange
                ? state.StuckCount > 0
                    ? $"残存 {DurableStateLines.Count} 件 (うち回収失敗 {state.StuckCount} 件)。接続先の変更と削除がブロックされています。回収失敗分は待っても解消しません。"
                    : $"残存 {DurableStateLines.Count} 件。接続先の変更と削除がブロックされています。期限を過ぎれば自動的に解消します。"
                : "残存データはありません。";
        }
        catch (ApiException ex)
        {
            DurableStateSummary = $"残存データを取得できません: {ex.Message}";
        }
    }

    private static string FormatErrorCode(string? errorCode)
        => string.IsNullOrWhiteSpace(errorCode) ? string.Empty : $" / {errorCode}";

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedId = Selected?.Id;
        var sharesTask = _api.GetAdminSharesAsync();
        var hostsTask = _api.GetAdminHostsAsync();
        await Task.WhenAll(sharesTask, hostsTask);
        ReplaceAll(Items, sharesTask.Result);
        ReplaceAll(Hosts, hostsTask.Result);
        if (selectedId.HasValue)
            Selected = Items.FirstOrDefault(s => s.Id == selectedId.Value);
        if (NewHostId is null || Hosts.All(h => h.Id != NewHostId.Value))
            NewHostId = Hosts.FirstOrDefault()?.Id;
    });

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (NewHostId is null) { StatusMessage = "対象ホストを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(NewShareName)) { StatusMessage = "共有名 (SMB) を入力してください。"; return; }
        if (string.IsNullOrWhiteSpace(NewDisplayName)) { StatusMessage = "表示名を入力してください。"; return; }
        await _api.CreateShareAsync(new CreateShareRequest { HostId = NewHostId.Value, ShareName = NewShareName, DisplayName = NewDisplayName });
        NewShareName = NewDisplayName = string.Empty;
        await RefreshAsync();
    }, successMessage: "共有を作成しました。");

    [RelayCommand]
    public Task SaveAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "保存する共有を選択してください。"; return; }
        if (EditHostId is null) { StatusMessage = "対象ホストを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(EditShareName)) { StatusMessage = "共有名 (SMB) を入力してください。"; return; }
        if (string.IsNullOrWhiteSpace(EditDisplayName)) { StatusMessage = "表示名を入力してください。"; return; }
        await _api.UpdateShareAsync(Selected.Id, new UpdateShareRequest
        {
            HostId = EditHostId.Value,
            ShareName = EditShareName,
            DisplayName = EditDisplayName,
        });
        await RefreshAsync();
    }, successMessage: "保存しました。");

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除する共有を選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"共有 \"{Selected.DisplayName}\" を削除しますか？\nこの共有に紐づく権限も削除されます。",
            "削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteShareAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "削除しました。");

    [RelayCommand]
    public Task ReleaseDurableStateAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "共有を選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"共有 \"{Selected.DisplayName}\" の未完了データ {DurableStateLines.Count} 件を強制解除しますか？\n\n" +
            "・進行中の転送があれば中断されます\n" +
            "・共有へ接続できない場合、一時ファイルは共有上にゴミとして残ります\n" +
            "・解除した内容と残ったパスは監査ログに記録されます",
            "強制解除の確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        var result = await _api.ReleaseShareDurableStateAsync(Selected.Id, new ReleaseDurableStateRequest
        {
            Confirm = true,
            Reason = "管理画面からの強制解除",
        });
        await LoadDurableStateAsync(Selected.Id);
        StatusMessage = result.Abandoned > 0
            ? $"解除しました。回収 {result.CleanedUp} 件 / 未回収 {result.Abandoned} 件。未回収分は共有上に残っている可能性があります (監査ログ参照)。"
            : $"解除しました。{result.CleanedUp} 件を回収済みです。";
    });
}
