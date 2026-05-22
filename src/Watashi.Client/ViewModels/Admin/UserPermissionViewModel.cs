using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.ViewModels.Admin;

public partial class UserPermissionViewModel : ObservableObject
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
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string browsePath = "/";

    public UserPermissionViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            Users.Clear(); foreach (var u in await _api.GetUsersAsync()) Users.Add(u);
            Shares.Clear(); foreach (var s in await _api.GetAdminSharesAsync()) Shares.Add(s);
            Templates.Clear(); foreach (var t in await _api.GetTemplatesAsync()) Templates.Add(t);
            await LoadItemsAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    partial void OnSelectedUserChanged(UserDto? value) => _ = LoadItemsAsync();

    [RelayCommand]
    public async Task LoadItemsAsync()
    {
        try
        {
            Items.Clear();
            foreach (var p in await _api.GetUserPermissionsAsync(SelectedUser?.Id)) Items.Add(p);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task BrowseAsync()
    {
        if (NewShare is null) return;
        try
        {
            var res = await _api.AdminBrowseAsync(NewShare.HostId, NewShare.Id, BrowsePath);
            BrowsePath = res.CurrentPath;
            BrowseEntries.Clear();
            foreach (var e in res.Entries.Where(e => e.Type == "directory")) BrowseEntries.Add(e);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        if (SelectedUser is null || NewShare is null || NewTemplate is null)
        {
            StatusMessage = "ユーザー / 共有 / テンプレートを選択してください。"; return;
        }
        try
        {
            await _api.CreateUserPermissionAsync(new CreateUserPermissionRequest
            {
                UserId = SelectedUser.Id, ShareId = NewShare.Id, TemplateId = NewTemplate.Id,
                AllowedPath = NewAllowedPath, DisplayName = NewDisplayName,
            });
            await LoadItemsAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _api.DeleteUserPermissionAsync(Selected.Id); await LoadItemsAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
