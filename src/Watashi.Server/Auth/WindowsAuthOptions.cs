namespace Watashi.Server.Auth;

/// <summary>Windows 統合認証をどのホスティング方式で受けるか。</summary>
public static class WindowsAuthModes
{
    /// <summary>無効。/api/auth/win/* を map しない。既定値。</summary>
    public const string None = "None";
    /// <summary>IIS + ASP.NET Core Module でホストする (本番構成)。IIS 側で Windows 認証を有効にする。</summary>
    public const string IIS = "IIS";
    /// <summary>Kestrel 直受け (Windows Service / dotnet run)。アプリ内で Negotiate を処理する。</summary>
    public const string Negotiate = "Negotiate";
}

/// <summary>OS アカウント名のドメイン部をどう扱うか。</summary>
public static class WindowsAuthDomainMatchModes
{
    /// <summary>ドメイン部を無視し、アカウント名だけで照合する。単一ドメインが保証された互換モード。</summary>
    public const string IgnoreDomain = "IgnoreDomain";
    /// <summary>AllowedDomains に列挙したドメインからのみ受け付ける。</summary>
    public const string AllowList = "AllowList";
}

/// <summary>
/// appsettings の Auth セクションから読む Windows 統合認証まわりの設定。
/// 既定値はすべて「無効・現状維持」側に倒してあり、明示的に設定を入れるまで
/// サーバーの挙動は一切変わらない。
/// </summary>
public class WindowsAuthOptions
{
    /// <summary><see cref="WindowsAuthModes"/> のいずれか。既定 None (機能オフ)。</summary>
    public string Mode { get; set; } = WindowsAuthModes.None;

    /// <summary>
    /// HTTP でも初回パスワード設定を許すか。既定 false。
    /// ローカル開発 (http://localhost) のためだけの逃げ道で、本番では必ず false のままにする。
    /// </summary>
    public bool AllowHttp { get; set; }

    /// <summary><see cref="WindowsAuthDomainMatchModes"/> のいずれか。既定 AllowList。</summary>
    public string DomainMatch { get; set; } = WindowsAuthDomainMatchModes.AllowList;

    /// <summary>DomainMatch = AllowList のときに許可するドメイン (NetBIOS 名・DNS 名どちらでも)。</summary>
    public IList<string> AllowedDomains { get; set; } = new List<string>();

    /// <summary>GET /api/auth/win/whoami を有効にするか。既定 false。</summary>
    public bool EnableDiagnostics { get; set; }

    /// <summary>
    /// Windows統合認証だけで通常ログイン用JWTを発行するか。既定false。
    /// falseでも初回パスワード設定は従来どおり利用できる。
    /// </summary>
    public bool EnableSso { get; set; }

    /// <summary>初回設定エンドポイントの IP あたり毎分許可数。既定 30。</summary>
    public int SetupPerMinutePerIp { get; set; } = 30;

    public bool IsEnabled => !string.Equals(Mode, WindowsAuthModes.None, StringComparison.OrdinalIgnoreCase);

    /// <summary>有効時の設定値を検証する。未知の値を既定動作へ暗黙フォールバックさせない。</summary>
    public void Validate()
    {
        if (!IsEnabled) return;

        if (!string.Equals(Mode, WindowsAuthModes.IIS, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Mode, WindowsAuthModes.Negotiate, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Auth:WindowsAuth:Mode の値 '{Mode}' は不正です。None / IIS / Negotiate のいずれかを指定してください。");

        if (!string.Equals(DomainMatch, WindowsAuthDomainMatchModes.IgnoreDomain, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(DomainMatch, WindowsAuthDomainMatchModes.AllowList, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Auth:WindowsAuth:DomainMatch の値 '{DomainMatch}' は不正です。IgnoreDomain / AllowList のいずれかを指定してください。");

        if (string.Equals(DomainMatch, WindowsAuthDomainMatchModes.AllowList, StringComparison.OrdinalIgnoreCase) &&
            !AllowedDomains.Any(domain => !string.IsNullOrWhiteSpace(domain)))
            throw new InvalidOperationException(
                "Windows 認証を AllowList で有効にする場合は Auth:WindowsAuth:AllowedDomains を1件以上指定してください。");
    }
}
