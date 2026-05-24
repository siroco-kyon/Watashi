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
        if (string.IsNullOrWhiteSpace(NewUsername)) { StatusMessage = "ユーザー名を入力してください。"; return; }
        if (string.IsNullOrWhiteSpace(NewPassword)) { StatusMessage = "初期パスワードを入力してください。"; return; }
        await _api.CreateUserAsync(new CreateUserRequest { Username = NewUsername, Password = NewPassword, IsAdmin = NewIsAdmin });
        NewUsername = NewPassword = string.Empty; NewIsAdmin = false;
        await RefreshAsync();
    }, successMessage: "ユーザーを作成しました。");

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除するユーザーを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"ユーザー \"{Selected.Username}\" を削除しますか？\nこのユーザーの権限・信頼デバイス・セッションも削除されます。",
            "削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteUserAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "削除しました。");

    [RelayCommand]
    public Task UnlockAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "ロック解除するユーザーを選択してください。"; return; }
        await _api.UnlockUserAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "ロック解除しました。");

    [RelayCommand]
    public Task ResetPasswordAsync(string newPw) => SafeAsync(async () =>
    {
        if (Selected is null || string.IsNullOrWhiteSpace(newPw)) return;
        await _api.ResetPasswordAsync(Selected.Id, new ResetPasswordRequest { NewPassword = newPw });
    }, successMessage: "リセットしました。");
}
