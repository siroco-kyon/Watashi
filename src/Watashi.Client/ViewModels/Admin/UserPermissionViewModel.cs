using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.ViewModels.Admin;

public partial class UserPermissionSummary : ObservableObject
{
    public UserDto User { get; }
    public int Id => User.Id;
    public string Username => User.Username;
    public bool IsAdmin => User.IsAdmin;
    [ObservableProperty] private int permissionCount;

    public UserPermissionSummary(UserDto user, int count)
    {
        User = user;
        permissionCount = count;
    }
}

public partial class UserPermissionViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    private readonly List<UserDto> _allUsers = new();
    private readonly List<ShareDto> _allShares = new();
    private readonly Dictionary<int, int> _countsByUser = new();

    public ObservableCollection<UserPermissionDto> Items { get; } = new();
    public ObservableCollection<UserPermissionSummary> UserSummaries { get; } = new();
    public ObservableCollection<HostDto> Hosts { get; } = new();
    public ObservableCollection<ShareDto> Shares { get; } = new();
    public ObservableCollection<PermissionTemplateDto> Templates { get; } = new();
    public ObservableCollection<FileEntry> BrowseEntries { get; } = new();

    [ObservableProperty] private UserDto? selectedUser;
    [ObservableProperty] private UserPermissionSummary? selectedSummary;
    [ObservableProperty] private HostDto? selectedHost;
    [ObservableProperty] private ShareDto? newShare;
    [ObservableProperty] private PermissionTemplateDto? newTemplate;
    [ObservableProperty] private string newAllowedPath = "/";
    [ObservableProperty] private string newDisplayName = string.Empty;
    [ObservableProperty] private UserPermissionDto? selected;
    [ObservableProperty] private string browsePath = "/";
    [ObservableProperty] private string userFilter = string.Empty;
    [ObservableProperty] private FileEntry? selectedBrowseEntry;

    public bool HasSelectedUser => SelectedUser is not null;
    public bool HasNoUsers => UserSummaries.Count == 0;
    public bool HasNoItems => Items.Count == 0 && HasSelectedUser;
    public string SelectedUserHeader => SelectedUser is null
        ? "ユーザーを選択してください"
        : $"👤 {SelectedUser.Username}{(SelectedUser.IsAdmin ? "  (管理者)" : "")}";

    public UserPermissionViewModel(ApiClient api)
    {
        _api = api;
        UserSummaries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoUsers));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoItems));
    }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedUserId = SelectedUser?.Id;
        var selectedHostId = SelectedHost?.Id;
        var newShareId = NewShare?.Id;
        var newTemplateId = NewTemplate?.Id;

        var usersTask = _api.GetUsersAsync();
        var hostsTask = _api.GetAdminHostsAsync();
        var sharesTask = _api.GetAdminSharesAsync();
        var templatesTask = _api.GetTemplatesAsync();
        var allPermsTask = _api.GetUserPermissionsAsync(null);
        await Task.WhenAll(usersTask, hostsTask, sharesTask, templatesTask, allPermsTask);

        RebuildCounts(allPermsTask.Result);
        _allUsers.Clear();
        _allUsers.AddRange(usersTask.Result);
        _allShares.Clear();
        _allShares.AddRange(sharesTask.Result);

        ReplaceAll(Hosts, hostsTask.Result);
        // 権限の強い順 (フルアクセス → 読取+書込 → 読取のみ → 個別)
        ReplaceAll(Templates, templatesTask.Result
            .OrderByDescending(t => Score(t)).ThenBy(t => t.Id));

        // ホスト復元 (前回選択 → 先頭)
        SelectedHost = selectedHostId.HasValue
            ? Hosts.FirstOrDefault(h => h.Id == selectedHostId.Value) ?? Hosts.FirstOrDefault()
            : Hosts.FirstOrDefault();
        // 共有はホスト変更時に絞り込まれる
        if (newShareId.HasValue)
            NewShare = Shares.FirstOrDefault(s => s.Id == newShareId.Value) ?? Shares.FirstOrDefault();

        NewTemplate = newTemplateId.HasValue
            ? Templates.FirstOrDefault(t => t.Id == newTemplateId.Value) ?? Templates.FirstOrDefault()
            : Templates.FirstOrDefault();

        ApplyUserFilter(selectedUserId);
        await LoadItemsAsync();
    });

    private static int Score(PermissionTemplateDto t) =>
        (t.CanRead ? 1 : 0) + (t.CanWrite ? 1 : 0) + (t.CanDelete ? 1 : 0) + (t.CanRename ? 1 : 0);

    partial void OnSelectedUserChanged(UserDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedUser));
        OnPropertyChanged(nameof(SelectedUserHeader));
        OnPropertyChanged(nameof(HasNoItems));
        _ = LoadItemsAsync();
    }

    partial void OnSelectedSummaryChanged(UserPermissionSummary? value)
    {
        SelectedUser = value?.User;
    }

    partial void OnSelectedHostChanged(HostDto? value)
    {
        if (value is null)
        {
            Shares.Clear();
            NewShare = null;
            BrowseEntries.Clear();
            BrowsePath = "/";
            return;
        }
        ReplaceAll(Shares, _allShares.Where(s => s.HostId == value.Id));
        NewShare = Shares.FirstOrDefault();
    }

    partial void OnNewShareChanged(ShareDto? value)
    {
        BrowseEntries.Clear();
        BrowsePath = "/";
    }

    partial void OnUserFilterChanged(string value) => ApplyUserFilter(SelectedUser?.Id);

    partial void OnSelectedBrowseEntryChanged(FileEntry? value)
    {
        if (value?.Type != FileEntryTypes.Directory) return;
        NewAllowedPath = RemotePaneViewModel.JoinPath(BrowsePath, value.Name);
    }

    [RelayCommand]
    public Task LoadItemsAsync() => SafeAsync(async () =>
    {
        if (SelectedUser is null) { Items.Clear(); return; }
        var items = await _api.GetUserPermissionsAsync(SelectedUser.Id);
        ReplaceAll(Items, items
            .OrderBy(i => i.HostName)
            .ThenBy(i => i.ShareName)
            .ThenBy(i => i.AllowedPath));
    });

    [RelayCommand]
    public Task BrowseAsync() => SafeAsync(async () =>
    {
        if (NewShare is null) { StatusMessage = "ブラウズには共有を選択してください。"; return; }
        var res = await _api.AdminBrowseAsync(NewShare.HostId, NewShare.Id, BrowsePath);
        BrowsePath = res.CurrentPath;
        ReplaceAll(BrowseEntries, res.Entries.Where(e => e.Type == FileEntryTypes.Directory));
        SelectedBrowseEntry = null;
    });

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (SelectedUser is null) { StatusMessage = "先にユーザーを選択してください。"; return; }
        if (SelectedHost is null) { StatusMessage = "サーバーを選択してください。"; return; }
        if (NewShare is null) { StatusMessage = "共有を選択してください。"; return; }
        if (NewTemplate is null) { StatusMessage = "権限テンプレートを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(NewAllowedPath)) { StatusMessage = "許可パスを入力してください。"; return; }
        await _api.CreateUserPermissionAsync(new CreateUserPermissionRequest
        {
            UserId = SelectedUser.Id, ShareId = NewShare.Id, TemplateId = NewTemplate.Id,
            AllowedPath = NewAllowedPath, DisplayName = NewDisplayName,
        });
        NewAllowedPath = "/";
        NewDisplayName = string.Empty;
        await RefreshCountsAsync();
        await LoadItemsAsync();
        StatusMessage = "パスを追加しました。";
    });

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除する付与済みパスを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"\"{Selected.Username}\" の {Selected.HostName} / {Selected.ShareName} / {Selected.AllowedPath} の権限を削除しますか？",
            "権限削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteUserPermissionAsync(Selected.Id);
        await RefreshCountsAsync();
        await LoadItemsAsync();
        StatusMessage = "削除しました。";
    });

    private async Task RefreshCountsAsync()
    {
        var all = await _api.GetUserPermissionsAsync(null);
        RebuildCounts(all);
        foreach (var s in UserSummaries)
            s.PermissionCount = _countsByUser.TryGetValue(s.Id, out var n) ? n : 0;
    }

    private void RebuildCounts(IEnumerable<UserPermissionDto> all)
    {
        _countsByUser.Clear();
        foreach (var p in all)
        {
            if (_countsByUser.TryGetValue(p.UserId, out var c)) _countsByUser[p.UserId] = c + 1;
            else _countsByUser[p.UserId] = 1;
        }
    }

    private void ApplyUserFilter(int? preferredUserId)
    {
        var filter = UserFilter.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? _allUsers
            : _allUsers.Where(u => u.Username.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var list = filtered.ToList();

        UserSummaries.Clear();
        foreach (var u in list)
            UserSummaries.Add(new UserPermissionSummary(u, _countsByUser.TryGetValue(u.Id, out var c) ? c : 0));

        var nextUser = preferredUserId.HasValue
            ? list.FirstOrDefault(u => u.Id == preferredUserId.Value) ?? list.FirstOrDefault()
            : list.FirstOrDefault();
        SelectedUser = nextUser;
        SelectedSummary = nextUser is null ? null : UserSummaries.FirstOrDefault(s => s.Id == nextUser.Id);
    }
}
