using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Client.ViewModels;

public partial class RemotePaneViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();

    // サーバから取得した全件 (Parent を除く)。表示用 Entries はここから絞り込んで作る。
    private readonly List<FileEntry> _all = new();
    private FileEntry? _parentEntry;

    public ObservableCollection<FileEntry> Entries { get; } = new();
    public ObservableCollection<LocationDto> Locations { get; } = new();

    [ObservableProperty] private LocationDto? selectedLocation;
    [ObservableProperty] private string currentPath = "/";
    [ObservableProperty] private FileEntry? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;
    [ObservableProperty] private bool canGoUp;
    [ObservableProperty] private string? sortKey;
    [ObservableProperty] private string filterText = string.Empty;

    public bool HasLocation => SelectedLocation is not null;
    public bool HasLocations => Locations.Count > 0;
    public bool HasNoLocations => Locations.Count == 0;
    public bool NeedsLocationSelection => Locations.Count > 0 && SelectedLocation is null;
    public bool HasNoFilterMatches =>
        SelectedLocation is not null &&
        !string.IsNullOrWhiteSpace(FilterText) &&
        !_all.Any(e => FileEntryFilter.Matches(e, FilterText));
    public bool IsFolderEmpty =>
        SelectedLocation is not null && string.IsNullOrWhiteSpace(FilterText) && _all.Count == 0;

    public RemotePaneViewModel(ApiClient api)
    {
        _api = api;
        Locations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLocations));
            OnPropertyChanged(nameof(HasNoLocations));
            OnPropertyChanged(nameof(NeedsLocationSelection));
        };
    }

    partial void OnSelectedLocationChanged(LocationDto? value)
    {
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(NeedsLocationSelection));
        OnPropertyChanged(nameof(HasNoFilterMatches));
        OnPropertyChanged(nameof(IsFolderEmpty));
        if (value is null)
        {
            _all.Clear();
            _parentEntry = null;
            Entries.Clear();
            Selected = null;
            CurrentPath = "/";
            CanGoUp = false;
            CanGoBack = false;
            CanGoForward = false;
            return;
        }
        _back.Clear();
        _forward.Clear();
        UpdateHistoryFlags();
        Selected = null;
        CurrentPath = value.Path;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task LoadHostsAndLocationsAsync()
    {
        try
        {
            IsBusy = true;
            var previous = SelectedLocation;
            var catalog = await _api.GetUserCatalogAsync();
            var loaded = new List<LocationDto>();
            foreach (var h in catalog.Hosts)
                foreach (var s in h.Shares)
                    foreach (var l in s.Locations)
                        loaded.Add(l);

            SelectedLocation = null;
            Locations.Clear();
            foreach (var l in loaded) Locations.Add(l);
            if (previous is not null)
            {
                SelectedLocation = Locations.FirstOrDefault(l => l.PermissionId == previous.PermissionId)
                    ?? Locations.FirstOrDefault(l =>
                        l.HostId == previous.HostId &&
                        l.ShareId == previous.ShareId &&
                        string.Equals(l.Path, previous.Path, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) { StatusMessage = "ロケーション取得失敗: " + ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>任意パスへ移動（履歴に積む）。</summary>
    [RelayCommand]
    public async Task NavigateAsync(string? newPath)
    {
        if (SelectedLocation is null) return;
        var target = PathHelper.NormalizePath(newPath ?? CurrentPath);
        if (string.Equals(target, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshAsync();
            return;
        }
        if (!string.IsNullOrEmpty(CurrentPath)) _back.Push(CurrentPath);
        _forward.Clear();
        CurrentPath = target;
        UpdateHistoryFlags();
        await RefreshAsync();
    }

    [RelayCommand]
    public Task GoBackAsync()
    {
        if (_back.Count == 0) return Task.CompletedTask;
        var prev = _back.Pop();
        if (!string.IsNullOrEmpty(CurrentPath)) _forward.Push(CurrentPath);
        CurrentPath = prev;
        UpdateHistoryFlags();
        return RefreshAsync();
    }

    [RelayCommand]
    public Task GoForwardAsync()
    {
        if (_forward.Count == 0) return Task.CompletedTask;
        var next = _forward.Pop();
        if (!string.IsNullOrEmpty(CurrentPath)) _back.Push(CurrentPath);
        CurrentPath = next;
        UpdateHistoryFlags();
        return RefreshAsync();
    }

    [RelayCommand]
    public Task GoUpAsync()
    {
        if (!CanGoUp) return Task.CompletedTask;
        var parent = PathHelper.GetParent(CurrentPath);
        if (parent == CurrentPath) return Task.CompletedTask;
        return NavigateAsync(parent);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (SelectedLocation is null) return;
        try
        {
            IsBusy = true;
            // サーバの 1 ページ上限 (200 件) を超えるフォルダでも全件取得する。
            // ソートはサーバ側で行うため、全ページで同じ sort を渡せば全体が正しく並ぶ。
            _all.Clear();
            _parentEntry = null;
            for (var page = 1; ; page++)
            {
                var res = await _api.ListFilesAsync(SelectedLocation.HostId, SelectedLocation.ShareId, CurrentPath, page, SortKey);
                if (page == 1)
                    _parentEntry = res.Entries.FirstOrDefault(x => x.Type == FileEntryTypes.Parent);
                var entries = res.Entries.Where(e => e.Type != FileEntryTypes.Parent).ToList();
                _all.AddRange(entries);
                if (_all.Count >= res.TotalCount || entries.Count == 0) break;
            }
            // 親へ戻れるかはサーバが parent entry の CanGoUp で示すので、それを採用。
            CanGoUp = _parentEntry?.CanGoUp == true;
            ApplyView();
            StatusMessage = string.Empty;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>_all から絞り込み (FilterText) を適用して表示用 Entries を作り直す。並びはサーバ側で確定済み。</summary>
    private void ApplyView()
    {
        Entries.Clear();
        if (_parentEntry is not null) Entries.Add(_parentEntry);
        foreach (var e in _all.Where(e => FileEntryFilter.Matches(e, FilterText)))
            Entries.Add(e);
        OnPropertyChanged(nameof(HasNoFilterMatches));
        OnPropertyChanged(nameof(IsFolderEmpty));
    }

    /// <summary>列ヘッダクリックで昇順 ⇄ 降順を切り替え、サーバから並べ直して取得する。</summary>
    public void SortBy(string column) => SortKey = FileEntrySort.Toggle(SortKey, column);

    partial void OnSortKeyChanged(string? value) => _ = RefreshAsync();
    partial void OnFilterTextChanged(string value) => ApplyView();

    [RelayCommand]
    public async Task OpenSelectedAsync()
    {
        if (Selected is null || SelectedLocation is null) return;
        if (Selected.Type == FileEntryTypes.Parent)
        {
            if (Selected.CanGoUp != true) return;
            await GoUpAsync();
            return;
        }
        if (Selected.Type == FileEntryTypes.Directory)
        {
            await NavigateAsync(JoinPath(CurrentPath, Selected.Name));
        }
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (Selected is null || SelectedLocation is null || Selected.Type == FileEntryTypes.Parent) return;
        if (!SelectedLocation.Permissions.Delete)
        {
            StatusMessage = "削除失敗: 削除権限がありません。";
            return;
        }
        var name = Selected.Name;
        try
        {
            await _api.DeleteFileAsync(SelectedLocation.HostId, SelectedLocation.ShareId, JoinPath(CurrentPath, name));
            await RefreshAsync();
            StatusMessage = $"削除しました: {name}";
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "削除失敗: " + ex.Message;
        }
        catch (Exception ex) { StatusMessage = "削除失敗: " + ex.Message; }
    }

    [RelayCommand]
    public async Task NewFolderAsync(string? name)
    {
        if (SelectedLocation is null || string.IsNullOrWhiteSpace(name)) return;
        if (!SelectedLocation.Permissions.Write)
        {
            StatusMessage = "フォルダ作成失敗: 書き込み権限がありません。";
            return;
        }
        try
        {
            await _api.MkdirAsync(SelectedLocation.HostId, SelectedLocation.ShareId, JoinPath(CurrentPath, name));
            await RefreshAsync();
            StatusMessage = $"フォルダを作成しました: {name}";
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "フォルダ作成失敗: " + ex.Message;
        }
        catch (Exception ex) { StatusMessage = "フォルダ作成失敗: " + ex.Message; }
    }

    /// <summary>
    /// 選択中アイテムを同フォルダ内でリネーム。サーバ側で「親ディレクトリの変更不可」「許可ルートそのものは
    /// リネーム不可」のチェックが入っているので、それらは StatusMessage に伝播するだけ。
    /// </summary>
    public async Task RenameSelectedAsync(string? newName)
    {
        if (Selected is null || SelectedLocation is null || Selected.Type == FileEntryTypes.Parent) return;
        if (string.IsNullOrWhiteSpace(newName) || newName == Selected.Name) return;
        if (!SelectedLocation.Permissions.Rename)
        {
            StatusMessage = "リネーム失敗: リネーム権限がありません。";
            return;
        }
        // 「/」「\」を含む名前は親ディレクトリ変更とみなされサーバが拒否する。クライアント側で先弾き。
        if (newName.Contains('/') || newName.Contains('\\'))
        {
            StatusMessage = "リネーム名に / や \\ は含められません";
            return;
        }
        var oldPath = JoinPath(CurrentPath, Selected.Name);
        var newPath = JoinPath(CurrentPath, newName);
        try
        {
            await _api.RenameAsync(SelectedLocation.HostId, SelectedLocation.ShareId, oldPath, newPath);
            await RefreshAsync();
            StatusMessage = $"リネーム: → {newName}";
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessage = "リネーム失敗: " + ex.Message;
        }
        catch (Exception ex) { StatusMessage = "リネーム失敗: " + ex.Message; }
    }

    private void UpdateHistoryFlags()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
    }

    public static string JoinPath(string parent, string name)
    {
        var p = parent.TrimEnd('/');
        return p == string.Empty ? "/" + name : p + "/" + name;
    }
}
