using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.ViewModels.Admin;

public partial class UserPermissionViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<UserPermissionDto> Items { get; } = new();
    public ObservableCollection<UserDto> Users { get; } = new();
    public ObservableCollection<ShareDto> Shares { get; } = new();
    public ObservableCollection<PermissionTemplateDto> Templates { get; } = new();
    public ObservableCollection<FileEntry> BrowseEntries { get; } = new();

    [ObservableProperty] private UserDto? selectedUser;
    [ObservableProperty] private ShareDto? newShare;
    [ObservableProperty] private PermissionTemplateDto? newTemplate;
    [ObservableProperty] private string newAllowedPath = "/";
    [ObservableProperty] private string newDisplayName = string.Empty;
    [ObservableProperty] private UserPermissionDto? selected;
    [ObservableProperty] private string browsePath = "/";

    public UserPermissionViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var usersTask = _api.GetUsersAsync();
        var sharesTask = _api.GetAdminSharesAsync();
        var templatesTask = _api.GetTemplatesAsync();
        await Task.WhenAll(usersTask, sharesTask, templatesTask);
        ReplaceAll(Users, usersTask.Result);
        ReplaceAll(Shares, sharesTask.Result);
        ReplaceAll(Templates, templatesTask.Result);
        await LoadItemsAsync();
    });

    partial void OnSelectedUserChanged(UserDto? value) => _ = LoadItemsAsync();

    [RelayCommand]
    public Task LoadItemsAsync() => SafeAsync(async () => ReplaceAll(Items, await _api.GetUserPermissionsAsync(SelectedUser?.Id)));

    [RelayCommand]
    public Task BrowseAsync() => SafeAsync(async () =>
    {
        if (NewShare is null) return;
        var res = await _api.AdminBrowseAsync(NewShare.HostId, NewShare.Id, BrowsePath);
        BrowsePath = res.CurrentPath;
        ReplaceAll(BrowseEntries, res.Entries.Where(e => e.Type == FileEntryTypes.Directory));
    });

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (SelectedUser is null || NewShare is null || NewTemplate is null)
        {
            StatusMessage = "ユーザー / 共有 / テンプレートを選択してください。"; return;
        }
        await _api.CreateUserPermissionAsync(new CreateUserPermissionRequest
        {
            UserId = SelectedUser.Id, ShareId = NewShare.Id, TemplateId = NewTemplate.Id,
            AllowedPath = NewAllowedPath, DisplayName = NewDisplayName,
        });
        await LoadItemsAsync();
    });

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.DeleteUserPermissionAsync(Selected.Id);
        await LoadItemsAsync();
    });
}
