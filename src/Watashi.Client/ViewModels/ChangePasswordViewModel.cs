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

    // 強制変更 (true) か任意変更 (false) か。表示文言とキャンセルボタンの有無を切り替える。
    // 既定は true: 呼び出し側が明示しない場合は安全側 (強制) として扱う。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderMessage))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool isMandatory = true;

    public string HeaderMessage => IsMandatory
        ? "⚠ パスワード変更が必要です"
        : "🔑 パスワードを変更します";

    /// <summary>任意変更のときだけキャンセル可能。強制変更では従来どおり閉じる手段を出さない。</summary>
    public bool CanCancel => !IsMandatory;

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
