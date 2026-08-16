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
    private readonly AppSettings _settings;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private CancellationTokenSource? _refreshCts;
    private long _refreshGeneration;
    private string? _nextListCursor;
    private CancellationTokenSource? _searchCts;
    private long _searchGeneration;
    private string? _nextSearchCursor;
    private bool _suppressLocationRecent;
    private string _lastSuccessfulPath = "/";

    // サーバから増分取得済みの項目 (Parent を除く)。表示用 Entries はここから絞り込む。
    private readonly List<FileEntry> _all = new();
    private FileEntry? _parentEntry;

    public ObservableCollection<FileEntry> Entries { get; } = new();
    public ObservableCollection<LocationDto> Locations { get; } = new();
    public ObservableCollection<RemotePlaceSetting> FavoritePlaces { get; } = new();
    public ObservableCollection<RemotePlaceSetting> RecentPlaces { get; } = new();
    public ObservableCollection<RemoteSearchResult> SearchResults { get; } = new();

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
    [ObservableProperty] private RemotePlaceSetting? selectedFavorite;
    [ObservableProperty] private RemotePlaceSetting? selectedRecent;
    [ObservableProperty] private bool hasMoreEntries;
    [ObservableProperty] private bool isLoadingMore;
    [ObservableProperty] private int loadedEntryCount;
    [ObservableProperty] private int totalEntryCount;
    [ObservableProperty] private bool isListTruncated;
    [ObservableProperty] private string listProgressText = string.Empty;
    [ObservableProperty] private string searchQuery = string.Empty;
    [ObservableProperty] private RemoteSearchResult? selectedSearchResult;
    [ObservableProperty] private bool isSearching;
    [ObservableProperty] private bool hasMoreSearchResults;
    [ObservableProperty] private string searchStatus = string.Empty;

    public bool HasLocation => SelectedLocation is not null;
    public bool HasLocations => Locations.Count > 0;
    public bool HasNoLocations => Locations.Count == 0;
    public bool HasFavorites => FavoritePlaces.Count > 0;
    public bool HasRecentPlaces => RecentPlaces.Count > 0;
    public bool HasSelectedFavorite => SelectedFavorite is not null;
    public bool HasSelectedRecent => SelectedRecent is not null;
    public bool HasSearchResults => SearchResults.Count > 0;
    public bool CanStartSearch => !IsSearching && SearchQuery.Trim().Length >= 2;
    public bool CanCancelSearch => IsSearching;
    public bool CanLoadMoreSearch => !IsSearching && HasMoreSearchResults;
    public bool HasSelectedSearchResult => SelectedSearchResult is not null;
    public bool CanSaveCurrentPlace => SelectedLocation is { } location &&
        Locations.Contains(location) &&
        PathHelper.IsPathWithin(location.Path, CurrentPath);
    public bool NeedsLocationSelection => Locations.Count > 0 && SelectedLocation is null;
    public bool HasNoFilterMatches =>
        SelectedLocation is not null &&
        !string.IsNullOrWhiteSpace(FilterText) &&
        !_all.Any(e => FileEntryFilter.Matches(e, FilterText));
    public bool IsFolderEmpty =>
        SelectedLocation is not null && string.IsNullOrWhiteSpace(FilterText) && _all.Count == 0;

    public RemotePaneViewModel(ApiClient api, AppSettings settings)
    {
        _api = api;
        _settings = settings;
        Locations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLocations));
            OnPropertyChanged(nameof(HasNoLocations));
            OnPropertyChanged(nameof(NeedsLocationSelection));
            OnPropertyChanged(nameof(CanSaveCurrentPlace));
        };
        FavoritePlaces.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFavorites));
        RecentPlaces.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentPlaces));
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSearchResults));
    }

    partial void OnSelectedLocationChanged(LocationDto? value)
    {
        CancelCurrentRefresh();
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(NeedsLocationSelection));
        OnPropertyChanged(nameof(HasNoFilterMatches));
        OnPropertyChanged(nameof(IsFolderEmpty));
        OnPropertyChanged(nameof(CanSaveCurrentPlace));
        if (value is null)
        {
            _all.Clear();
            _parentEntry = null;
            Entries.Clear();
            Selected = null;
            CurrentPath = "/";
            _lastSuccessfulPath = "/";
            CanGoUp = false;
            CanGoBack = false;
            CanGoForward = false;
            ResetListPaging();
            IsBusy = false;
            return;
        }
        // Never leave entries from the previous permission/location actionable while the new
        // location is loading or if that load fails.
        _all.Clear();
        _parentEntry = null;
        Entries.Clear();
        ResetListPaging();
        CanGoUp = false;
        _back.Clear();
        _forward.Clear();
        UpdateHistoryFlags();
        Selected = null;
        CurrentPath = value.Path;
        _lastSuccessfulPath = PathHelper.NormalizePath(value.Path);
        var recordRecent = !_suppressLocationRecent;
        _ = RefreshCoreAsync(recordRecent);
    }

    partial void OnCurrentPathChanged(string value) => OnPropertyChanged(nameof(CanSaveCurrentPlace));

    partial void OnSelectedFavoriteChanged(RemotePlaceSetting? value) =>
        OnPropertyChanged(nameof(HasSelectedFavorite));

    partial void OnSelectedRecentChanged(RemotePlaceSetting? value) =>
        OnPropertyChanged(nameof(HasSelectedRecent));

    partial void OnSelectedSearchResultChanged(RemoteSearchResult? value) =>
        OnPropertyChanged(nameof(HasSelectedSearchResult));

    partial void OnSearchQueryChanged(string value) => OnPropertyChanged(nameof(CanStartSearch));

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartSearch));
        OnPropertyChanged(nameof(CanCancelSearch));
        OnPropertyChanged(nameof(CanLoadMoreSearch));
    }

    partial void OnHasMoreSearchResultsChanged(bool value) =>
        OnPropertyChanged(nameof(CanLoadMoreSearch));

    [RelayCommand]
    public async Task LoadHostsAndLocationsAsync()
    {
        CancelCurrentSearch(clearResults: true);
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

            // 保存済みの場所は、現在サーバが返した同一 PermissionId/host/share の
            // 許可ルート配下だけを公開する。期限切れ・削除・ルート変更もここで除去する。
            var settingsChanged = _settings.ReconcileRemotePlaces(loaded);
            SyncSavedPlaces();
            if (settingsChanged) TrySaveSettings("利用できなくなった保存場所を整理しました。");

            if (previous is not null)
            {
                _suppressLocationRecent = true;
                try
                {
                    SelectedLocation = Locations.FirstOrDefault(l => l.PermissionId == previous.PermissionId)
                        ?? Locations.FirstOrDefault(l =>
                            l.HostId == previous.HostId &&
                            l.ShareId == previous.ShareId &&
                            string.Equals(l.Path, previous.Path, StringComparison.OrdinalIgnoreCase));
                }
                finally { _suppressLocationRecent = false; }
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
        if (!PathHelper.IsPathWithin(SelectedLocation.Path, target))
        {
            CurrentPath = _lastSuccessfulPath;
            StatusMessage = "この権限の許可ルート外には移動できません。";
            return;
        }
        if (string.Equals(target, _lastSuccessfulPath, StringComparison.OrdinalIgnoreCase))
        {
            CurrentPath = target;
            await RefreshCoreAsync(recordRecent: true);
            return;
        }
        if (!string.IsNullOrEmpty(_lastSuccessfulPath)) _back.Push(_lastSuccessfulPath);
        _forward.Clear();
        Selected = null;
        CurrentPath = target;
        UpdateHistoryFlags();
        await RefreshCoreAsync(recordRecent: true);
    }

    [RelayCommand]
    public Task GoBackAsync()
    {
        if (_back.Count == 0 || SelectedLocation is null) return Task.CompletedTask;
        var prev = _back.Pop();
        if (!PathHelper.IsPathWithin(SelectedLocation.Path, prev))
        {
            UpdateHistoryFlags();
            return Task.CompletedTask;
        }
        if (!string.IsNullOrEmpty(_lastSuccessfulPath)) _forward.Push(_lastSuccessfulPath);
        Selected = null;
        CurrentPath = prev;
        UpdateHistoryFlags();
        return RefreshCoreAsync(recordRecent: true);
    }

    [RelayCommand]
    public Task GoForwardAsync()
    {
        if (_forward.Count == 0 || SelectedLocation is null) return Task.CompletedTask;
        var next = _forward.Pop();
        if (!PathHelper.IsPathWithin(SelectedLocation.Path, next))
        {
            UpdateHistoryFlags();
            return Task.CompletedTask;
        }
        if (!string.IsNullOrEmpty(_lastSuccessfulPath)) _back.Push(_lastSuccessfulPath);
        Selected = null;
        CurrentPath = next;
        UpdateHistoryFlags();
        return RefreshCoreAsync(recordRecent: true);
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
    public Task RefreshAsync() => RefreshCoreAsync(recordRecent: false);

    private async Task RefreshCoreAsync(bool recordRecent)
    {
        var location = SelectedLocation;
        if (location is null) return;

        var path = PathHelper.NormalizePath(CurrentPath);
        if (!PathHelper.IsPathWithin(location.Path, path))
        {
            CurrentPath = _lastSuccessfulPath;
            StatusMessage = "この権限の許可ルート外には移動できません。";
            return;
        }
        CurrentPath = path;
        var sort = SortKey;
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCts, cts)?.Cancel();
        try
        {
            IsBusy = true;
            ResetListPaging();
            var res = await _api.ListFilesIncrementalAsync(
                location.PermissionId,
                location.HostId,
                location.ShareId,
                path,
                sort,
                limit: 200,
                ct: cts.Token);

            if (generation != Volatile.Read(ref _refreshGeneration) ||
                !ReferenceEquals(location, SelectedLocation) ||
                !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sort, SortKey, StringComparison.Ordinal))
                return;

            _all.Clear();
            _all.AddRange(res.Entries);
            _parentEntry = new FileEntry
            {
                Name = "..",
                Type = FileEntryTypes.Parent,
                CanGoUp = res.CanGoUp,
            };
            CanGoUp = res.CanGoUp;
            _nextListCursor = res.NextCursor;
            HasMoreEntries = res.HasMore;
            LoadedEntryCount = res.LoadedCount;
            TotalEntryCount = res.TotalCount;
            IsListTruncated = res.Truncated;
            UpdateListProgress();
            _lastSuccessfulPath = path;
            ApplyView();
            StatusMessage = res.Truncated
                ? "このフォルダーは上限を超えたため、先頭10万件まで表示できます。"
                : string.Empty;
            if (recordRecent) RecordSuccessfulPlace(location, path);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                CurrentPath = _lastSuccessfulPath;
                StatusMessage = ex.Message;
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                Interlocked.CompareExchange(ref _refreshCts, null, cts);
                IsBusy = false;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        var cursor = _nextListCursor;
        var location = SelectedLocation;
        if (IsBusy || IsLoadingMore || !HasMoreEntries ||
            string.IsNullOrWhiteSpace(cursor) || location is null)
            return;

        var generation = Volatile.Read(ref _refreshGeneration);
        var path = PathHelper.NormalizePath(CurrentPath);
        var sort = SortKey;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCts, cts)?.Cancel();
        try
        {
            IsLoadingMore = true;
            var res = await _api.ListFilesIncrementalAsync(
                location.PermissionId,
                location.HostId,
                location.ShareId,
                path,
                sort,
                limit: 200,
                cursor: cursor,
                ct: cts.Token);

            if (generation != Volatile.Read(ref _refreshGeneration) ||
                !ReferenceEquals(location, SelectedLocation) ||
                !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sort, SortKey, StringComparison.Ordinal))
                return;

            _all.AddRange(res.Entries);
            _nextListCursor = res.NextCursor;
            HasMoreEntries = res.HasMore;
            LoadedEntryCount = res.LoadedCount;
            TotalEntryCount = res.TotalCount;
            IsListTruncated = res.Truncated;
            UpdateListProgress();
            ApplyView();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Gone)
        {
            _nextListCursor = null;
            HasMoreEntries = false;
            UpdateListProgress();
            StatusMessage = "一覧の続きを保持する期限が切れました。再読み込みしてください。";
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
                StatusMessage = "一覧の追加読込に失敗しました: " + ex.Message;
        }
        finally
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                Interlocked.CompareExchange(ref _refreshCts, null, cts);
                IsLoadingMore = false;
            }
            cts.Dispose();
        }
    }

    private void ResetListPaging()
    {
        _nextListCursor = null;
        HasMoreEntries = false;
        LoadedEntryCount = 0;
        TotalEntryCount = 0;
        IsListTruncated = false;
        UpdateListProgress();
    }

    private void UpdateListProgress()
    {
        ListProgressText = TotalEntryCount == 0
            ? string.Empty
            : $"{LoadedEntryCount:N0} / {TotalEntryCount:N0} 件を読み込み済み" +
              (IsListTruncated ? "（上限10万件）" : string.Empty);
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
        if (IsBusy || Selected is null || SelectedLocation is null || Selected.Type == FileEntryTypes.Parent) return;
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
        if (IsBusy || SelectedLocation is null || string.IsNullOrWhiteSpace(name)) return;
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
        if (IsBusy || Selected is null || SelectedLocation is null || Selected.Type == FileEntryTypes.Parent) return;
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

    /// <summary>
    /// 選択項目を同じ接続場所内の別フォルダーへコピーする。コピー元は変更しない。
    /// 同名衝突はサーバー側で安全な別名を確保し、フォルダーは完成後にだけ公開される。
    /// </summary>
    public async Task CopyEntriesAsync(IReadOnlyList<FileEntry> entries, string? targetDirectory)
    {
        if (IsBusy || SelectedLocation is null || entries.Count == 0 || string.IsNullOrWhiteSpace(targetDirectory))
            return;
        if (!SelectedLocation.Permissions.Read || !SelectedLocation.Permissions.Write)
        {
            StatusMessage = "コピー失敗: 読み取り権限と書き込み権限が必要です。";
            return;
        }

        string destination;
        try { destination = PathHelper.NormalizePath(targetDirectory); }
        catch (Exception ex)
        {
            StatusMessage = "コピー先パスが不正です: " + ex.Message;
            return;
        }

        var copied = 0;
        try
        {
            IsBusy = true;
            foreach (var entry in entries.Where(e => e.Type != FileEntryTypes.Parent))
            {
                StatusMessage = $"コピー中 ({copied + 1}/{entries.Count}): {entry.Name}";
                await _api.CopyRemoteAsync(new RemoteCopyRequest
                {
                    SourceHostId = SelectedLocation.HostId,
                    SourceShareId = SelectedLocation.ShareId,
                    SourcePath = JoinPath(CurrentPath, entry.Name),
                    TargetHostId = SelectedLocation.HostId,
                    TargetShareId = SelectedLocation.ShareId,
                    TargetPath = JoinPath(destination, entry.Name),
                    CollisionPolicy = "rename",
                });
                copied++;
            }
            await RefreshAsync();
            StatusMessage = $"{copied:N0}件をコピーしました（コピー元は変更していません）。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"コピー失敗（{copied:N0}件完了）: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void AddCurrentFavorite()
    {
        if (!CanSaveCurrentPlace || SelectedLocation is null)
        {
            StatusMessage = "お気に入りには許可されたリモート場所を表示してから追加してください。";
            return;
        }

        var place = CreatePlace(SelectedLocation, CurrentPath);
        var changed = _settings.AddRemoteFavorite(place);
        SyncSavedPlaces();
        SelectedFavorite = FavoritePlaces.FirstOrDefault(p => AppSettings.SameRemotePlace(p, place));
        if (!changed)
        {
            StatusMessage = "現在の場所は既にお気に入りです。";
            return;
        }
        if (TrySaveSettings()) StatusMessage = "現在の場所をお気に入りに追加しました。";
    }

    [RelayCommand]
    public void RemoveFavorite(RemotePlaceSetting? place)
    {
        if (place is null)
        {
            StatusMessage = "削除するお気に入りを選択してください。";
            return;
        }
        if (!_settings.RemoveRemoteFavorite(place)) return;
        SyncSavedPlaces();
        if (TrySaveSettings()) StatusMessage = "お気に入りから削除しました。";
    }

    [RelayCommand]
    public async Task OpenSavedPlaceAsync(RemotePlaceSetting? place)
    {
        if (place is null) return;
        var location = FindCurrentLocation(place);
        if (location is null)
        {
            // 起動後に権限一覧が変わった場合にも、古い保存場所を移動入口として使わせない。
            if (_settings.ReconcileRemotePlaces(Locations))
            {
                SyncSavedPlaces();
                TrySaveSettings();
            }
            StatusMessage = "この保存場所は現在の権限では利用できないため除外しました。";
            return;
        }

        if (!ReferenceEquals(SelectedLocation, location)) SelectedLocation = location;
        await NavigateAsync(place.Path);
    }

    private void RecordSuccessfulPlace(LocationDto location, string path)
    {
        var place = CreatePlace(location, path);
        if (!_settings.RecordRecentRemotePlace(place)) return;
        SyncSavedPlaces();
        TrySaveSettings();
    }

    private LocationDto? FindCurrentLocation(RemotePlaceSetting place)
        => Locations.FirstOrDefault(location =>
            location.PermissionId == place.PermissionId &&
            location.HostId == place.HostId &&
            location.ShareId == place.ShareId &&
            PathHelper.IsPathWithin(location.Path, place.Path));

    private static RemotePlaceSetting CreatePlace(LocationDto location, string path)
    {
        var normalizedPath = PathHelper.NormalizePath(path);
        var root = PathHelper.NormalizePath(location.Path);
        var locationName = string.IsNullOrWhiteSpace(location.DisplayName)
            ? $"{location.HostName} / {location.ShareName}"
            : location.DisplayName.Trim();
        var displayName = string.Equals(root, normalizedPath, StringComparison.OrdinalIgnoreCase)
            ? locationName
            : $"{locationName} / {RelativePath(root, normalizedPath)}";
        return new RemotePlaceSetting
        {
            PermissionId = location.PermissionId,
            HostId = location.HostId,
            ShareId = location.ShareId,
            Path = normalizedPath,
            DisplayName = displayName,
        };
    }

    private static string RelativePath(string root, string path)
        => root == "/" ? path.TrimStart('/') : path[(root.Length + 1)..];

    private void SyncSavedPlaces()
    {
        var favoriteSelection = SelectedFavorite;
        var recentSelection = SelectedRecent;

        FavoritePlaces.Clear();
        foreach (var place in _settings.RemoteFavorites) FavoritePlaces.Add(place);
        RecentPlaces.Clear();
        foreach (var place in _settings.RecentRemotePlaces) RecentPlaces.Add(place);

        SelectedFavorite = favoriteSelection is null
            ? FavoritePlaces.FirstOrDefault()
            : FavoritePlaces.FirstOrDefault(p => AppSettings.SameRemotePlace(p, favoriteSelection))
              ?? FavoritePlaces.FirstOrDefault();
        SelectedRecent = recentSelection is null
            ? RecentPlaces.FirstOrDefault()
            : RecentPlaces.FirstOrDefault(p => AppSettings.SameRemotePlace(p, recentSelection))
              ?? RecentPlaces.FirstOrDefault();
    }

    private bool TrySaveSettings(string? successMessage = null)
    {
        try
        {
            _settings.Save();
            if (!string.IsNullOrWhiteSpace(successMessage)) StatusMessage = successMessage;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "お気に入り/最近使った場所の保存に失敗しました: " + ex.Message;
            return false;
        }
    }

    [RelayCommand]
    public async Task SearchRemoteAsync()
    {
        if (IsSearching) return;
        var query = SearchQuery.Trim();
        if (query.Length < 2)
        {
            SearchStatus = "検索語を2文字以上入力してください。";
            return;
        }

        var generation = Interlocked.Increment(ref _searchGeneration);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _searchCts, cts)?.Cancel();
        try
        {
            IsSearching = true;
            SearchResults.Clear();
            SelectedSearchResult = null;
            _nextSearchCursor = null;
            HasMoreSearchResults = false;
            SearchStatus = "権限のある場所を検索しています…";
            var response = await _api.SearchRemoteAsync(new RemoteSearchRequest
            {
                Query = query,
                Limit = 100,
            }, cts.Token);

            if (generation != Volatile.Read(ref _searchGeneration) ||
                !string.Equals(query, SearchQuery.Trim(), StringComparison.Ordinal))
                return;
            ApplySearchResponse(response, append: false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _searchGeneration))
                SearchStatus = "検索に失敗しました: " + ex.Message;
        }
        finally
        {
            if (generation == Volatile.Read(ref _searchGeneration))
            {
                Interlocked.CompareExchange(ref _searchCts, null, cts);
                IsSearching = false;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    public async Task LoadMoreSearchAsync()
    {
        var cursor = _nextSearchCursor;
        if (IsSearching || !HasMoreSearchResults || string.IsNullOrWhiteSpace(cursor)) return;

        var generation = Volatile.Read(ref _searchGeneration);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _searchCts, cts)?.Cancel();
        try
        {
            IsSearching = true;
            var response = await _api.SearchRemoteAsync(new RemoteSearchRequest
            {
                Cursor = cursor,
                Limit = 100,
            }, cts.Token);
            if (generation != Volatile.Read(ref _searchGeneration)) return;
            ApplySearchResponse(response, append: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Gone)
        {
            _nextSearchCursor = null;
            HasMoreSearchResults = false;
            SearchStatus = "検索結果の保持期限または権限が変わりました。もう一度検索してください。";
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _searchGeneration))
                SearchStatus = "検索結果の追加読込に失敗しました: " + ex.Message;
        }
        finally
        {
            if (generation == Volatile.Read(ref _searchGeneration))
            {
                Interlocked.CompareExchange(ref _searchCts, null, cts);
                IsSearching = false;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    public void CancelSearch()
    {
        if (!IsSearching) return;
        _searchCts?.Cancel();
        SearchStatus = "検索の取消を要求しました。フォルダー列挙中は完了後に停止します。";
    }

    [RelayCommand]
    public async Task OpenSearchResultAsync(RemoteSearchResult? result)
    {
        if (result is null) return;
        var location = Locations.FirstOrDefault(x =>
            x.PermissionId == result.PermissionId &&
            x.HostId == result.HostId &&
            x.ShareId == result.ShareId &&
            x.Permissions.Read &&
            string.Equals(
                PathHelper.NormalizePath(x.Path),
                PathHelper.NormalizePath(result.LocationRoot),
                StringComparison.OrdinalIgnoreCase) &&
            PathHelper.IsPathWithin(x.Path, result.FullPath));
        if (location is null)
        {
            SearchStatus = "この検索結果は現在の権限では開けません。もう一度検索してください。";
            return;
        }

        if (!ReferenceEquals(SelectedLocation, location)) SelectedLocation = location;
        await NavigateAsync(result.ParentPath);
        Selected = Entries.FirstOrDefault(x =>
            x.Type != FileEntryTypes.Parent &&
            string.Equals(x.Name, result.Name, StringComparison.OrdinalIgnoreCase));
        SearchStatus = Selected is not null
            ? $"場所を開きました: {result.FullPath}"
            : $"場所を開きました。項目が未読込の場合は「さらに読み込む」を使用してください: {result.ParentPath}";
    }

    private void ApplySearchResponse(RemoteSearchResponse response, bool append)
    {
        if (!append) SearchResults.Clear();
        foreach (var result in response.Results) SearchResults.Add(result);
        _nextSearchCursor = response.NextCursor;
        HasMoreSearchResults = response.HasMore;

        var status = $"{response.LoadedCount:N0} 件表示 / {response.ScannedCount:N0} 件走査";
        if (response.Warnings.Count > 0)
            status += $"（{response.Warnings.Count:N0} 個の場所で一部失敗）";
        if (response.Truncated)
            status += " — " + (response.TruncationReason switch
            {
                "scan_limit" => "走査上限に達しました",
                "result_limit" => "結果上限に達しました",
                "timeout" => "時間上限に達しました",
                "depth_limit" => "深さ上限に達しました",
                _ => "一部の場所を検索できませんでした",
            });
        else if (response.MatchedCount == 0)
            status += " — 一致する項目はありません";
        SearchStatus = status;
    }

    private void UpdateHistoryFlags()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
    }

    private void CancelCurrentRefresh()
    {
        Interlocked.Increment(ref _refreshGeneration);
        Interlocked.Exchange(ref _refreshCts, null)?.Cancel();
        IsLoadingMore = false;
    }

    private void CancelCurrentSearch(bool clearResults)
    {
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Exchange(ref _searchCts, null)?.Cancel();
        IsSearching = false;
        _nextSearchCursor = null;
        HasMoreSearchResults = false;
        if (!clearResults) return;
        SearchResults.Clear();
        SelectedSearchResult = null;
        SearchStatus = string.Empty;
    }

    public static string JoinPath(string parent, string name)
    {
        var p = parent.TrimEnd('/');
        return p == string.Empty ? "/" + name : p + "/" + name;
    }
}
