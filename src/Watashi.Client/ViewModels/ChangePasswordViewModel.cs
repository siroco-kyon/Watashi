using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

public partial class ChangePasswordViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly SessionManager _session;
    [ObservableProperty] private string currentPassword = string.Empty;
    [ObservableProperty] private string newPassword = string.Empty;
    [ObservableProperty] private string confirmPassword = string.Empty;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    public event Action? Completed;

    public ChangePasswordViewModel(ApiClient api, SessionManager session)
    {
        _api = api;
        _session = session;
    }

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
            // サーバは新しい access/refresh token を返してくる。古いトークンは mcp claim 付きで
            // ミドルウェアに弾かれ、古い refresh token もサーバ側で失効済みなので必ず置き換える。
            var res = await _api.ChangePasswordAsync(CurrentPassword, NewPassword);
            _session.SetFromLogin(res);
            Completed?.Invoke();
        }
        catch (ApiException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }
}
