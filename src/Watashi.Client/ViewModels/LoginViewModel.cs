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

    /// <summary>
    /// ID を確定して 2 段目 (パスワード入力) に進んだか。
    /// false の間は ID と「次へ」だけを見せる。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnteringUsername))]
    private bool isPasswordStep;

    public bool IsEnteringUsername => !IsPasswordStep;

    public bool CanRemember => true;
    public bool IsHttpConnection => _settings.IsHttp;
    public string HttpTransportWarning => AppSettings.HttpTransportWarning;
    public string RememberTooltip => _settings.IsHttps
        ? "この PC を記憶し、次回以降は自動ログインします。"
        : "HTTP 接続ではデバイストークンが平文で通信されます。サーバー設定により自動ログインが禁止される場合があります。";
    public string WindowsSsoTooltip => _settings.IsHttps
        ? "現在のWindowsアカウントを使ってログインします（サーバーで有効な場合）。"
        : "HTTP接続ではサーバー設定によりWindows SSOが拒否される場合があります。通常のパスワードログインは利用できます。";

    public event Action<LoginResponse>? LoggedIn;

    /// <summary>
    /// 初回パスワード設定が必要になった。ユーザー名と受付期限を渡す。
    /// View 側でダイアログを出し、成功したらログイン応答を、中断されたら null を返すこと。
    /// </summary>
    public event Func<string, DateTime?, LoginResponse?>? PasswordSetupRequested;

    public LoginViewModel(
        ApiClient api,
        SessionManager session,
        AppSettings settings)
    {
        _api = api;
        _session = session;
        _settings = settings;
    }

    // ID を編集し直したらパスワード段階を解除する。別の ID には別の判定が要るため。
    partial void OnUsernameChanged(string value)
    {
        // 初回設定ダイアログをキャンセルした直後など、まだパスワード段階でなくても
        // 別 ID の入力中に前の利用者向けメッセージを残さない。
        StatusMessage = string.Empty;
        if (!IsPasswordStep) return;
        IsPasswordStep = false;
        Password = string.Empty;
    }

    /// <summary>
    /// 1 段目。ID について「パスワードを訊く」か「初回設定させる」かをサーバーに判定させる。
    /// 判定できない環境 (ドメイン非参加・機能無効・旧サーバー) では ApiClient が
    /// password にフォールバックするので、ここでは常に画面が進む。
    /// </summary>
    [RelayCommand]
    private async Task WindowsSsoLogin()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = "Windowsアカウントを確認しています…";
            var response = await _api.WindowsSsoLoginAsync();
            _session.SetFromLogin(response);
            LoggedIn?.Invoke(response);
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.StatusCode switch
            {
                HttpStatusCode.NotFound => "Windows SSOはこのサーバーで有効になっていません。パスワードでログインしてください。",
                HttpStatusCode.Unauthorized => "現在のWindowsアカウントではログインできません。パスワードでログインしてください。",
                HttpStatusCode.Forbidden when ex.Message == "account_disabled" =>
                    "アカウントが無効化されています。管理者に連絡してください。",
                HttpStatusCode.Forbidden when ex.Message == "account_locked" =>
                    "アカウントがロックされています。管理者に連絡してください。",
                HttpStatusCode.Forbidden =>
                    "Windows SSOはこの接続では許可されていません。パスワードでログインしてください。",
                _ => "Windows SSOに失敗しました: " + ex.Message,
            };
        }
        catch (Exception ex)
        {
            StatusMessage = "Windows SSOに失敗しました: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task Continue()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(Username))
        {
            StatusMessage = "ユーザー名を入力してください。";
            return;
        }
        try
        {
            IsBusy = true;
            StatusMessage = string.Empty;

            // 通信中に ID が編集されても、判定した ID と別の利用者を初回設定へ
            // 渡さないよう、この操作で使う値を固定する。
            var requestedUsername = Username.Trim();
            var prepared = await _api.PrepareLoginAsync(requestedUsername);
            if (!string.Equals(Username.Trim(), requestedUsername, StringComparison.Ordinal))
                return;
            if (prepared.Mode == LoginModes.Setup)
            {
                // 初回設定に成功するとサーバーがそのままログイン応答を返し、
                // SessionManager も設定済みになる。以降は通常ログインと同じ扱いでよい。
                // 「このPCを記憶」も設定完了後に呼び出し側が処理する。
                var setupResult = PasswordSetupRequested?.Invoke(requestedUsername, prepared.SetupExpiresAt);
                if (setupResult is not null)
                {
                    LoggedIn?.Invoke(setupResult);
                    return;
                }
                // 中断された場合はパスワード入力へは進めず、ID 入力に留める。
                StatusMessage = "初回パスワードの設定が完了していません。";
                return;
            }

            IsPasswordStep = true;
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

    [RelayCommand]
    private async Task Login()
    {
        if (IsBusy) return;
        // Enter 連打などで 1 段目から直接呼ばれた場合は、まず判定に回す。
        if (!IsPasswordStep)
        {
            await Continue();
            return;
        }
        try
        {
            IsBusy = true;
            StatusMessage = string.Empty;

            var res = await _api.LoginAsync(Username.Trim(), Password);
            _session.SetFromLogin(res);
            LoggedIn?.Invoke(res);
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "ユーザー名またはパスワードが違います。",
                HttpStatusCode.Forbidden when ex.Message == "account_disabled" =>
                    "アカウントが無効化されています。管理者に連絡してください。",
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
