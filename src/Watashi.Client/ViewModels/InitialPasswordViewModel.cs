using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Client.ViewModels;

/// <summary>
/// 初回パスワード設定。
///
/// <see cref="ChangePasswordViewModel"/> とはあえて分けている。あちらは「現在のパスワード」を
/// 必ず送る前提で作られており、モード分岐で共用すると現在のパスワードを空で送る経路が
/// できてしまう。ここでは現在のパスワードという概念自体が存在しない (まだ無いので)。
/// </summary>
public partial class InitialPasswordViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly SessionManager _session;

    [ObservableProperty] private string username = string.Empty;
    [ObservableProperty] private string newPassword = string.Empty;
    [ObservableProperty] private string confirmPassword = string.Empty;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    /// <summary>初回設定の受付期限。null なら無期限で、画面には出さない。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeadline))]
    [NotifyPropertyChangedFor(nameof(DeadlineMessage))]
    private DateTime? setupExpiresAt;

    public bool HasDeadline => SetupExpiresAt.HasValue;

    public string DeadlineMessage => SetupExpiresAt is DateTime due
        ? $"設定の期限: {due.ToLocalTime():yyyy/MM/dd HH:mm} まで"
        : string.Empty;

    /// <summary>設定が完了し、ログイン済みのトークンを受け取った。</summary>
    public event Action<LoginResponse>? Completed;

    public InitialPasswordViewModel(ApiClient api, SessionManager session)
    {
        _api = api;
        _session = session;
    }

    [RelayCommand]
    private async Task Submit()
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
            // 成功時はそのままログイン済みのトークンが返る。改めてログインし直す必要はない。
            var res = await _api.InitializePasswordAsync(Username, NewPassword);
            _session.SetFromLogin(res);
            Completed?.Invoke(res);
        }
        catch (ApiException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = "通信エラー: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
