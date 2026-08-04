namespace Watashi.Shared.DTOs.Auth;

/// <summary>ログイン画面が「次に何を訊けばよいか」を決めるためのモード。</summary>
public static class LoginModes
{
    /// <summary>通常どおりパスワードを入力させる。未知のユーザー名・本人不一致もすべてこれになる。</summary>
    public const string Password = "password";
    /// <summary>初回パスワード設定画面を出す。本人確認が取れた未設定アカウントのときだけ返る。</summary>
    public const string Setup = "setup";
}

public class PrepareLoginRequest
{
    public string Username { get; set; } = string.Empty;
}

public class PrepareLoginResponse
{
    /// <summary><see cref="LoginModes"/> のいずれか。</summary>
    public string Mode { get; set; } = LoginModes.Password;
    /// <summary>初回設定の受付期限 (Mode=setup のときのみ)。null は無期限。</summary>
    public DateTime? SetupExpiresAt { get; set; }
}

public class InitializePasswordRequest
{
    public string Username { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    /// <summary>監査用のマシン名。任意。</summary>
    public string? MachineName { get; set; }
}

/// <summary>
/// Windows 統合認証の疎通確認用 (Auth:WindowsAuth:EnableDiagnostics = true のときだけ有効)。
/// 呼び出した本人の情報しか返さないため、ユーザー列挙には使えない。
/// </summary>
public class WindowsAuthDiagnosticsResponse
{
    /// <summary>OS から届いた生のアカウント名 (例: CORP\G012345)。</summary>
    public string? RawAccountName { get; set; }
    /// <summary>照合に使う正規化後の名前 (例: G012345)。</summary>
    public string? NormalizedName { get; set; }
    /// <summary>切り出したドメイン部 (例: CORP)。取れない場合は null。</summary>
    public string? Domain { get; set; }
    /// <summary>現在のドメイン照合設定を通過したか。</summary>
    public bool DomainAllowed { get; set; }
    /// <summary>正規化名と一致する Watashi ユーザーが存在するか。</summary>
    public bool WatashiUserExists { get; set; }
    /// <summary>そのユーザーが初回パスワード設定待ちか。</summary>
    public bool IsPasswordSetupPending { get; set; }
    /// <summary>サーバーの現在の Windows 認証モード (None / IIS / Negotiate)。</summary>
    public string WindowsAuthMode { get; set; } = string.Empty;
}
