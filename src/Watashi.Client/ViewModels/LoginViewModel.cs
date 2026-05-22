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
    private readonly CredentialStore _credStore;

    [ObservableProperty] private string username = string.Empty;
    [ObservableProperty] private string password = string.Empty;
    [ObservableProperty] private bool rememberDevice;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    public bool CanRemember => _settings.IsHttps;
    public string RememberTooltip => _settings.IsHttps ? "デバイス記憶 (HTTPS のみ)" : "HTTP 接続では使用できません";

    public event Action<LoginResponse>? LoggedIn;

    public LoginViewModel(ApiClient api, SessionManager session, AppSettings settings, CredentialStore cred)
    {
        _api = api; _session = session; _settings = settings; _credStore = cred;
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

            if (RememberDevice && _settings.IsHttps && !res.MustChangePassword)
            {
                try
                {
                    var td = await _api.TrustDeviceAsync(Environment.MachineName, Environment.UserName);
                    _credStore.SaveDeviceToken(Environment.MachineName, Environment.UserName, td.DeviceToken);
                }
                catch (Exception ex) { StatusMessage = "デバイス登録に失敗: " + ex.Message; }
            }
            LoggedIn?.Invoke(res);
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "ユーザー名またはパスワードが違います。",
                System.Net.HttpStatusCode.Forbidden => "アカウントがロックされています。管理者に連絡してください。",
                _ => "ログイン失敗: " + ex.Message,
            };
        }
        catch (Exception ex) { StatusMessage = "通信エラー: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
