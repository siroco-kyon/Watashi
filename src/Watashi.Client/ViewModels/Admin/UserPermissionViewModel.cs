using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
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
    public string? DisplayName => User.DisplayName;
    public string DisplayLabel => User.DisplayLabel;
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
    public ICollectionView ItemsView { get; }
    public ObservableCollection<UserPermissionSummary> UserSummaries { get; } = new();
    public ObservableCollection<HostDto> Hosts { get; } = new();
    public ObservableCollection<ShareDto> Shares { get; } = new();
    public ObservableCollection<PermissionTemplateDto> Templates { get; } = new();
    public ObservableCollection<FileEntry> BrowseEntries { get; } = new();
    public ObservableCollection<PermissionBundleDto> Bundles { get; } = new();
    public ObservableCollection<UserDto> CopyFromCandidates { get; } = new();
    public ObservableCollection<AdminSortOption> UserSortOptions { get; } = new()
    {
        new("ユーザー名", nameof(UserPermissionSummary.Username)),
        new("名前", nameof(UserPermissionSummary.DisplayName)),
        new("ID", nameof(UserPermissionSummary.Id)),
        new("権限数", nameof(UserPermissionSummary.PermissionCount), ListSortDirection.Descending),
        new("管理者", nameof(UserPermissionSummary.IsAdmin), ListSortDirection.Descending),
    };
    public ObservableCollection<AdminSortOption> PermissionSortOptions { get; } = new()
    {
        new("ホスト", nameof(UserPermissionDto.HostName)),
        new("共有", nameof(UserPermissionDto.ShareName)),
        new("パス", nameof(UserPermissionDto.AllowedPath)),
        new("テンプレ", nameof(UserPermissionDto.TemplateName)),
        new("表示名", nameof(UserPermissionDto.DisplayName)),
        new("状態", nameof(UserPermissionDto.EffectiveStatus)),
        new("有効期限", nameof(UserPermissionDto.ExpiresAt)),
        new("作成日時", nameof(UserPermissionDto.CreatedAt), ListSortDirection.Descending),
    };

    [ObservableProperty] private UserDto? selectedUser;
    [ObservableProperty] private UserPermissionSummary? selectedSummary;
    [ObservableProperty] private HostDto? selectedHost;
    [ObservableProperty] private ShareDto? newShare;
    [ObservableProperty] private PermissionTemplateDto? newTemplate;
    [ObservableProperty] private string newAllowedPath = "/";
    [ObservableProperty] private string newDisplayName = string.Empty;
    [ObservableProperty] private string newValidFromText = string.Empty;
    [ObservableProperty] private string newExpiresAtText = string.Empty;
    [ObservableProperty] private string newReason = string.Empty;
    [ObservableProperty] private string newTicketNumber = string.Empty;
    [ObservableProperty] private UserPermissionDto? selected;
    [ObservableProperty] private PermissionTemplateDto? editTemplate;
    [ObservableProperty] private string editAllowedPath = "/";
    [ObservableProperty] private string editDisplayName = string.Empty;
    [ObservableProperty] private string editValidFromText = string.Empty;
    [ObservableProperty] private string editExpiresAtText = string.Empty;
    [ObservableProperty] private string editReason = string.Empty;
    [ObservableProperty] private string editTicketNumber = string.Empty;
    [ObservableProperty] private string browsePath = "/";
    [ObservableProperty] private string userFilter = string.Empty;
    [ObservableProperty] private string permissionSearchText = string.Empty;
    [ObservableProperty] private AdminSortOption? selectedUserSortOption;
    [ObservableProperty] private AdminSortOption? selectedPermissionSortOption;
    [ObservableProperty] private FileEntry? selectedBrowseEntry;
    [ObservableProperty] private PermissionBundleDto? selectedBundle;
    [ObservableProperty] private UserDto? copyFromUser;
    [ObservableProperty] private ShareDto? simulationShare;
    [ObservableProperty] private string simulationPath = "/";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSimulationResult))]
    [NotifyPropertyChangedFor(nameof(SimulationWarningSummary))]
    private PermissionSimulationResponse? simulationResult;

    public bool HasSelectedUser => SelectedUser is not null;
    public bool HasSelectedPermission => Selected is not null;
    public bool HasSimulationResult => SimulationResult is not null;
    public string SimulationWarningSummary => SimulationResult?.Warnings.Count > 0
        ? string.Join(" / ", SimulationResult.Warnings.Select(w => w.Message).Distinct())
        : string.Empty;
    public string LocalDateTimeHint => "ローカル時刻 yyyy-MM-dd HH:mm[:ss]（空欄は制限なし）";
    public bool HasNoUsers => UserSummaries.Count == 0;
    public bool HasNoItems => Items.Count == 0 && HasSelectedUser;
    public string SelectedUserHeader => SelectedUser is null
        ? "ユーザーを選択してください"
        : $"👤 {SelectedUser.DisplayLabel}{(SelectedUser.IsAdmin ? "  (管理者)" : "")}";

    public UserPermissionViewModel(ApiClient api)
    {
        _api = api;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is UserPermissionDto p && MatchesSearch(
            PermissionSearchText, p.Id, p.Username, p.UserDisplayName, p.HostName, p.ShareName, p.AllowedPath, p.TemplateName,
            p.DisplayName, p.EffectiveStatus, p.EffectiveStatusLabel, p.ValidFrom, p.ExpiresAt,
            p.Reason, p.TicketNumber, p.WarningSummary);
        SelectedUserSortOption = UserSortOptions[0];
        SelectedPermissionSortOption = PermissionSortOptions[0];
        ApplySort(ItemsView, SelectedPermissionSortOption);
        UserSummaries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoUsers));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoItems));
    }

    partial void OnPermissionSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedUserSortOptionChanged(AdminSortOption? value) => ApplyUserFilter(SelectedUser?.Id);

    partial void OnSelectedPermissionSortOptionChanged(AdminSortOption? value) => ApplySort(ItemsView, value);

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedUserId = SelectedUser?.Id;
        var selectedHostId = SelectedHost?.Id;
        var newShareId = NewShare?.Id;
        var newTemplateId = NewTemplate?.Id;
        var simulationShareId = SimulationShare?.Id;

        var usersTask = _api.GetUsersAsync();
        var hostsTask = _api.GetAdminHostsAsync();
        var sharesTask = _api.GetAdminSharesAsync();
        var templatesTask = _api.GetTemplatesAsync();
        var allPermsTask = _api.GetUserPermissionsAsync(null);
        var bundlesTask = _api.GetBundlesAsync();
        await Task.WhenAll(usersTask, hostsTask, sharesTask, templatesTask, allPermsTask, bundlesTask);
        ReplaceAll(Bundles, bundlesTask.Result);

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
        SimulationShare = simulationShareId.HasValue
            ? Shares.FirstOrDefault(s => s.Id == simulationShareId.Value) ?? Shares.FirstOrDefault()
            : Shares.FirstOrDefault();

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
        SimulationResult = null;
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
            SimulationShare = null;
            BrowseEntries.Clear();
            BrowsePath = "/";
            return;
        }
        ReplaceAll(Shares, _allShares.Where(s => s.HostId == value.Id));
        NewShare = Shares.FirstOrDefault();
        SimulationShare = Shares.FirstOrDefault();
    }

    partial void OnNewShareChanged(ShareDto? value)
    {
        BrowseEntries.Clear();
        BrowsePath = "/";
    }

    partial void OnSelectedChanged(UserPermissionDto? value)
    {
        OnPropertyChanged(nameof(HasSelectedPermission));
        EditTemplate = value is null ? null : Templates.FirstOrDefault(t => t.Id == value.TemplateId);
        EditAllowedPath = value?.AllowedPath ?? "/";
        EditDisplayName = value?.DisplayName ?? string.Empty;
        EditValidFromText = FormatLocal(value?.ValidFrom);
        EditExpiresAtText = FormatLocal(value?.ExpiresAt);
        EditReason = value?.Reason ?? string.Empty;
        EditTicketNumber = value?.TicketNumber ?? string.Empty;
    }

    partial void OnUserFilterChanged(string value) => ApplyUserFilter(SelectedUser?.Id);

    partial void OnSimulationShareChanged(ShareDto? value) => SimulationResult = null;

    partial void OnSimulationPathChanged(string value) => SimulationResult = null;

    partial void OnSelectedBrowseEntryChanged(FileEntry? value)
    {
        if (value?.Type != FileEntryTypes.Directory) return;
        NewAllowedPath = RemotePaneViewModel.JoinPath(BrowsePath, value.Name);
    }

    [RelayCommand]
    public Task LoadItemsAsync() => SafeAsync(async () =>
    {
        if (SelectedUser is null) { Items.Clear(); return; }
        var selectedId = Selected?.Id;
        ReplaceAll(Items, await _api.GetUserPermissionsAsync(SelectedUser.Id));
        Selected = selectedId.HasValue ? Items.FirstOrDefault(p => p.Id == selectedId.Value) : null;
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
        if (!TryParseWindow(NewValidFromText, NewExpiresAtText, out var validFrom, out var expiresAt, out var error))
        {
            StatusMessage = error!;
            return;
        }
        if (!ValidateMetadataLengths(NewReason, NewTicketNumber, out error))
        {
            StatusMessage = error!;
            return;
        }
        var result = await _api.CreateUserPermissionAsync(new CreateUserPermissionRequest
        {
            UserId = SelectedUser.Id,
            ShareId = NewShare.Id,
            TemplateId = NewTemplate.Id,
            AllowedPath = NewAllowedPath,
            DisplayName = NewDisplayName,
            ValidFrom = validFrom,
            ExpiresAt = expiresAt,
            Reason = NewReason,
            TicketNumber = NewTicketNumber,
        });
        NewAllowedPath = "/";
        NewDisplayName = string.Empty;
        NewValidFromText = string.Empty;
        NewExpiresAtText = string.Empty;
        NewReason = string.Empty;
        NewTicketNumber = string.Empty;
        await RefreshCountsAsync();
        await LoadItemsAsync();
        Selected = Items.FirstOrDefault(p => p.Id == result.Id);
        StatusMessage = MutationMessage("パスを追加しました。", result.Warnings);
    });

    [RelayCommand]
    public Task UpdateSelectedAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "編集する権限を選択してください。"; return; }
        if (EditTemplate is null) { StatusMessage = "権限テンプレートを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(EditAllowedPath)) { StatusMessage = "許可パスを入力してください。"; return; }
        if (!TryParseWindow(EditValidFromText, EditExpiresAtText, out var validFrom, out var expiresAt, out var error))
        {
            StatusMessage = error!;
            return;
        }
        if (!ValidateMetadataLengths(EditReason, EditTicketNumber, out error))
        {
            StatusMessage = error!;
            return;
        }

        var id = Selected.Id;
        var result = await _api.UpdateUserPermissionAsync(id, new UpdateUserPermissionRequest
        {
            ShareId = Selected.ShareId,
            TemplateId = EditTemplate.Id,
            AllowedPath = EditAllowedPath,
            DisplayName = EditDisplayName,
            ValidFrom = validFrom,
            ExpiresAt = expiresAt,
            Reason = EditReason,
            TicketNumber = EditTicketNumber,
        });
        await LoadItemsAsync();
        Selected = Items.FirstOrDefault(p => p.Id == id);
        StatusMessage = MutationMessage("権限を更新しました。", result.Warnings);
    });

    [RelayCommand]
    public Task SimulateAsync() => SafeAsync(async () =>
    {
        if (SelectedUser is null) { StatusMessage = "シミュレーションするユーザーを選択してください。"; return; }
        if (SimulationShare is null) { StatusMessage = "シミュレーションする共有を選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(SimulationPath)) { StatusMessage = "シミュレーションするパスを入力してください。"; return; }

        SimulationResult = await _api.SimulatePermissionAsync(
            SelectedUser.Id, SimulationShare.Id, SimulationPath);
        StatusMessage = "実効権限を確認しました（データは変更していません）。";
    });

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除する付与済みパスを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"\"{Selected.UserDisplayLabel}\" の {Selected.HostName} / {Selected.ShareName} / {Selected.AllowedPath} の権限を削除しますか？",
            "権限削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteUserPermissionAsync(Selected.Id);
        await RefreshCountsAsync();
        await LoadItemsAsync();
        StatusMessage = "削除しました。";
    });

    /// <summary>選択中のセットを SelectedUser に適用 (重複は上書き or スキップを確認)。</summary>
    [RelayCommand]
    public Task ApplyBundleAsync() => SafeAsync(async () =>
    {
        if (SelectedUser is null) { StatusMessage = "先にユーザーを選択してください。"; return; }
        if (SelectedBundle is null) { StatusMessage = "適用するセットを選択してください。"; return; }
        var owMsg = System.Windows.MessageBox.Show(
            $"セット \"{SelectedBundle.Name}\" ({SelectedBundle.Entries.Count} 行) を {SelectedUser.DisplayLabel} に適用します。\n\n" +
            "[はい] 既存の重複行も上書き (テンプレ・表示名を更新)\n" +
            "[いいえ] 重複行はスキップ (推奨)\n" +
            "[キャンセル] 中止",
            "セット適用", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);
        if (owMsg == System.Windows.MessageBoxResult.Cancel) return;
        var overwrite = owMsg == System.Windows.MessageBoxResult.Yes;
        var res = await _api.ApplyBundleAsync(SelectedBundle.Id, new ApplyPermissionBundleRequest
        {
            UserId = SelectedUser.Id,
            Overwrite = overwrite,
        });
        await RefreshCountsAsync();
        await LoadItemsAsync();
        StatusMessage = $"セット適用: 追加 {res.Created} / 更新 {res.Updated} / スキップ {res.Skipped}";
    });

    /// <summary>選択中のユーザーから現ユーザーへ権限をコピー (全件)。</summary>
    [RelayCommand]
    public Task CopyFromUserAsync() => SafeAsync(async () =>
    {
        if (SelectedUser is null) { StatusMessage = "コピー先のユーザーを選択してください。"; return; }
        if (CopyFromUser is null) { StatusMessage = "コピー元のユーザーを選択してください。"; return; }
        if (CopyFromUser.Id == SelectedUser.Id) { StatusMessage = "コピー元とコピー先が同じです。"; return; }
        var owMsg = System.Windows.MessageBox.Show(
            $"{CopyFromUser.DisplayLabel} の全権限を {SelectedUser.DisplayLabel} にコピーします。\n\n" +
            "[はい] 既存の重複行も上書き\n" +
            "[いいえ] 重複行はスキップ (推奨)\n" +
            "[キャンセル] 中止",
            "権限コピー", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);
        if (owMsg == System.Windows.MessageBoxResult.Cancel) return;
        var overwrite = owMsg == System.Windows.MessageBoxResult.Yes;
        var res = await _api.CopyUserPermissionsAsync(new CopyUserPermissionsRequest
        {
            FromUserId = CopyFromUser.Id,
            ToUserId = SelectedUser.Id,
            Overwrite = overwrite,
        });
        await RefreshCountsAsync();
        await LoadItemsAsync();
        StatusMessage = $"権限コピー: 追加 {res.Copied} / 更新 {res.Updated} / スキップ {res.Skipped}";
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
            : _allUsers.Where(u =>
                u.Username.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (u.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        var list = SortUsers(filtered).ToList();

        UserSummaries.Clear();
        foreach (var u in list)
            UserSummaries.Add(new UserPermissionSummary(u, _countsByUser.TryGetValue(u.Id, out var c) ? c : 0));

        // 「他ユーザーからコピー」の候補は フィルタ非依存の全ユーザー
        ReplaceAll(CopyFromCandidates, _allUsers);

        var nextUser = preferredUserId.HasValue
            ? list.FirstOrDefault(u => u.Id == preferredUserId.Value) ?? list.FirstOrDefault()
            : list.FirstOrDefault();
        SelectedUser = nextUser;
        SelectedSummary = nextUser is null ? null : UserSummaries.FirstOrDefault(s => s.Id == nextUser.Id);
    }

    private IEnumerable<UserDto> SortUsers(IEnumerable<UserDto> users)
    {
        var option = SelectedUserSortOption ?? UserSortOptions[0];
        var descending = option.Direction == ListSortDirection.Descending;
        return option.PropertyName switch
        {
            nameof(UserPermissionSummary.Id) => descending
                ? users.OrderByDescending(u => u.Id)
                : users.OrderBy(u => u.Id),
            nameof(UserPermissionSummary.PermissionCount) => descending
                ? users.OrderByDescending(u => _countsByUser.TryGetValue(u.Id, out var c) ? c : 0).ThenBy(u => u.Username)
                : users.OrderBy(u => _countsByUser.TryGetValue(u.Id, out var c) ? c : 0).ThenBy(u => u.Username),
            nameof(UserPermissionSummary.IsAdmin) => descending
                ? users.OrderByDescending(u => u.IsAdmin).ThenBy(u => u.Username)
                : users.OrderBy(u => u.IsAdmin).ThenBy(u => u.Username),
            nameof(UserPermissionSummary.DisplayName) => descending
                ? users.OrderByDescending(UserSortName).ThenByDescending(u => u.Username)
                : users.OrderBy(UserSortName).ThenBy(u => u.Username),
            _ => descending
                ? users.OrderByDescending(u => u.Username)
                : users.OrderBy(u => u.Username),
        };
    }

    private static string UserSortName(UserDto user)
        => string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;

    private static bool TryParseWindow(
        string validFromText,
        string expiresAtText,
        out DateTime? validFromUtc,
        out DateTime? expiresAtUtc,
        out string? error)
    {
        validFromUtc = null;
        expiresAtUtc = null;
        error = null;
        if (!TryParseLocalUtc(validFromText, "有効開始日時", out validFromUtc, out error) ||
            !TryParseLocalUtc(expiresAtText, "有効期限", out expiresAtUtc, out error))
            return false;
        if (validFromUtc.HasValue && expiresAtUtc.HasValue && validFromUtc.Value >= expiresAtUtc.Value)
        {
            error = "有効期限は有効開始日時より後にしてください。";
            return false;
        }
        return true;
    }

    private static bool TryParseLocalUtc(
        string text,
        string fieldName,
        out DateTime? utc,
        out string? error)
    {
        utc = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTime.TryParseExact(text.Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            error = $"{fieldName}は yyyy-MM-dd HH:mm[:ss] 形式で入力してください。";
            return false;
        }
        utc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        return true;
    }

    private static bool ValidateMetadataLengths(string reason, string ticketNumber, out string? error)
    {
        if (reason.Trim().Length > CreateUserPermissionRequest.MaxReasonLength)
        {
            error = $"理由は {CreateUserPermissionRequest.MaxReasonLength} 文字以内で入力してください。";
            return false;
        }
        if (ticketNumber.Trim().Length > CreateUserPermissionRequest.MaxTicketNumberLength)
        {
            error = $"チケット/申請番号は {CreateUserPermissionRequest.MaxTicketNumberLength} 文字以内で入力してください。";
            return false;
        }
        error = null;
        return true;
    }

    private static string FormatLocal(DateTime? utc)
        => utc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string MutationMessage(string success, IReadOnlyCollection<PermissionWarningDto> warnings)
        => warnings.Count == 0
            ? success
            : success + " ⚠ " + string.Join(" / ", warnings.Select(w => w.Message).Distinct());
}
