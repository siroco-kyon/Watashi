using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class UserManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<UserDto> Items { get; } = new();
    [ObservableProperty] private UserDto? selected;
    [ObservableProperty] private string newUsername = string.Empty;
    [ObservableProperty] private string newPassword = string.Empty;
    [ObservableProperty] private bool newIsAdmin;

    public UserManagementViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () => ReplaceAll(Items, await _api.GetUsersAsync()));

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        await _api.CreateUserAsync(new CreateUserRequest { Username = NewUsername, Password = NewPassword, IsAdmin = NewIsAdmin });
        NewUsername = NewPassword = string.Empty; NewIsAdmin = false;
        await RefreshAsync();
    });

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.DeleteUserAsync(Selected.Id);
        await RefreshAsync();
    });

    [RelayCommand]
    public Task UnlockAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.UnlockUserAsync(Selected.Id);
        await RefreshAsync();
    });

    [RelayCommand]
    public Task ResetPasswordAsync(string newPw) => SafeAsync(async () =>
    {
        if (Selected is null || string.IsNullOrWhiteSpace(newPw)) return;
        await _api.ResetPasswordAsync(Selected.Id, new ResetPasswordRequest { NewPassword = newPw });
    }, successMessage: "リセットしました。");
}
