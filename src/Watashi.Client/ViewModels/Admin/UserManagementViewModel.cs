using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class UserManagementViewModel : ObservableObject
{
    private readonly ApiClient _api;
    public ObservableCollection<UserDto> Items { get; } = new();
    [ObservableProperty] private UserDto? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string newUsername = string.Empty;
    [ObservableProperty] private string newPassword = string.Empty;
    [ObservableProperty] private bool newIsAdmin;

    public UserManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            Items.Clear();
            foreach (var u in await _api.GetUsersAsync()) Items.Add(u);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        try
        {
            await _api.CreateUserAsync(new CreateUserRequest { Username = NewUsername, Password = NewPassword, IsAdmin = NewIsAdmin });
            NewUsername = NewPassword = string.Empty; NewIsAdmin = false;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _api.DeleteUserAsync(Selected.Id); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task UnlockAsync()
    {
        if (Selected is null) return;
        try { await _api.UnlockUserAsync(Selected.Id); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task ResetPasswordAsync(string newPw)
    {
        if (Selected is null || string.IsNullOrWhiteSpace(newPw)) return;
        try { await _api.ResetPasswordAsync(Selected.Id, new ResetPasswordRequest { NewPassword = newPw }); StatusMessage = "リセットしました。"; }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
