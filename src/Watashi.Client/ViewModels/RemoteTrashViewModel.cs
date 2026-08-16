using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.ViewModels;

public partial class RemoteTrashViewModel : ObservableObject
{
    private const int PageSize = 100;
    private readonly ApiClient _api;
    private readonly SessionManager _session;
    private int? _hostId;
    private int? _shareId;

    public ObservableCollection<RemoteTrashEntryDto> Items { get; } = new();
    public IReadOnlyList<TrashCollisionOption> CollisionOptions { get; } = new[]
    {
        new TrashCollisionOption(TrashCollisionPolicies.Fail, "同名なら中止"),
        new TrashCollisionOption(TrashCollisionPolicies.Rename, "別名で復元"),
        new TrashCollisionOption(TrashCollisionPolicies.Overwrite, "上書き（管理者）"),
    };

    [ObservableProperty] private RemoteTrashEntryDto? selected;
    [ObservableProperty] private TrashCollisionOption selectedCollisionPolicy;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private long totalBytes;

    public bool IsAdmin => _session.IsAdmin;
    public bool HasSelection => Selected is not null;
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page * PageSize < TotalCount;
    public string RangeText => TotalCount == 0
        ? "0 件"
        : $"{((Page - 1) * PageSize) + 1}–{Math.Min(Page * PageSize, TotalCount)} / {TotalCount} 件";

    public RemoteTrashViewModel(ApiClient api, SessionManager session)
    {
        _api = api;
        _session = session;
        selectedCollisionPolicy = CollisionOptions[0];
    }

    public Task InitializeAsync(int? hostId, int? shareId)
    {
        _hostId = hostId;
        _shareId = shareId;
        Page = 1;
        return RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var selectedId = Selected?.Id;
            var response = await _api.ListRemoteTrashAsync(_hostId, _shareId, Page, PageSize);
            Items.Clear();
            foreach (var item in response.Items) Items.Add(item);
            TotalCount = response.TotalCount;
            TotalBytes = response.TotalBytes;
            Page = response.Page;
            Selected = selectedId.HasValue ? Items.FirstOrDefault(x => x.Id == selectedId.Value) : null;
            NotifyPaging();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (!HasPrevious) return;
        Page--;
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (!HasNext) return;
        Page++;
        await RefreshAsync();
    }

    public async Task RestoreSelectedAsync()
    {
        if (Selected is null || IsBusy) return;
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var response = await _api.RestoreRemoteTrashAsync(
                Selected.Id, SelectedCollisionPolicy.Value);
            StatusMessage = $"{response.RestoredPath} へ復元しました。";
            await ReloadAfterMutationAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task PurgeSelectedAsync()
    {
        if (Selected is null || !IsAdmin || IsBusy) return;
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            await _api.PurgeRemoteTrashAsync(Selected.Id);
            StatusMessage = "完全削除しました。";
            await ReloadAfterMutationAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAfterMutationAsync()
    {
        // IsBusy中でも内部再読込できるよう、RefreshAsyncと同じ取得を直接行う。
        var targetPage = Page;
        var response = await _api.ListRemoteTrashAsync(_hostId, _shareId, targetPage, PageSize);
        if (response.Items.Count == 0 && targetPage > 1)
            response = await _api.ListRemoteTrashAsync(_hostId, _shareId, --targetPage, PageSize);
        Items.Clear();
        foreach (var item in response.Items) Items.Add(item);
        Page = response.Page;
        TotalCount = response.TotalCount;
        TotalBytes = response.TotalBytes;
        Selected = null;
        NotifyPaging();
    }

    partial void OnPageChanged(int value) => NotifyPaging();
    partial void OnTotalCountChanged(int value) => NotifyPaging();
    partial void OnSelectedChanged(RemoteTrashEntryDto? value) => OnPropertyChanged(nameof(HasSelection));

    private void NotifyPaging()
    {
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(RangeText));
    }

    public sealed record TrashCollisionOption(string Value, string Label);
}
