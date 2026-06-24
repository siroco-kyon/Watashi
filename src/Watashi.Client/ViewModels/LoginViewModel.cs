using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly SessionManager _session;
    private readonly AppSettings _settings;

    [ObservableProperty] private string username = string.Empty;
    [ObservableProperty] private string password = string.Empty;
    [ObservableProperty] private bool rememberDevice;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    public bool CanRemember => true;
    public string RememberTooltip => _settings.IsHttps
        ? "この PC を記憶し、次回以降は自動ログインします。"
        : "HTTP 接続ではデバイストークンが平文で通信されます。サーバー設定により自動ログインが禁止される場合があります。";

    public event Action<LoginResponse>? LoggedIn;

    public LoginViewModel(ApiClient api, SessionManager session, AppSettings settings)
    {
        _api = api;
        _session = session;
        _settings = settings;
    }

    [RelayCommand]
    private async Task Login()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = string.Empty;

            var res = await _api.LoginAsync(Username, Password);
            _session.SetFromLogin(res);
            LoggedIn?.Invoke(res);
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "ユーザー名またはパスワードが違います。",
                HttpStatusCode.Forbidden => "アカウントがロックされています。管理者に連絡してください。",
                _ => "ログイン失敗: " + ex.Message,
            };
        }
        catch (Exception ex)
        {
            StatusMessage = "通信エラー: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
