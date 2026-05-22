using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

public partial class ChangePasswordViewModel : ObservableObject
{
    private readonly ApiClient _api;
    [ObservableProperty] private string currentPassword = string.Empty;
    [ObservableProperty] private string newPassword = string.Empty;
    [ObservableProperty] private string confirmPassword = string.Empty;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    public event Action? Completed;

    public ChangePasswordViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    private async Task Change()
    {
        if (IsBusy) return;
        if (NewPassword != ConfirmPassword)
        {
            StatusMessage = "新しいパスワードと確認が一致しません。";
            return;
        }
        try
        {
            IsBusy = true;
            StatusMessage = string.Empty;
            await _api.ChangePasswordAsync(CurrentPassword, NewPassword);
            Completed?.Invoke();
        }
        catch (ApiException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }
}
