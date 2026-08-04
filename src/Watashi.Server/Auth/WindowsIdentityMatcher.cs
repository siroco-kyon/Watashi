namespace Watashi.Server.Auth;

/// <summary>
/// Windows 統合認証で得た OS アカウント名を Watashi ユーザー名 (= 社内 GID) と突き合わせる。
///
/// OS から届く名前は環境により 3 通りある。
///   NetBIOS 形式  CORP\G012345
///   UPN 形式      G012345@corp.example.com
///   名前のみ      G012345          (ワークグループのローカルアカウントなど)
/// いずれも「アカウント名部分」を取り出して大文字小文字を無視して比較する。
///
/// 副作用が無い純粋な判定処理なので、ドメイン参加環境が無くても全形式をテストできる。
/// </summary>
public static class WindowsIdentityMatcher
{
    /// <summary>OS アカウント名を (ドメイン部, アカウント名部) に分解する。分解できない場合はドメイン部が null。</summary>
    public static (string? Domain, string Account) Split(string? rawAccountName)
    {
        var raw = rawAccountName?.Trim() ?? string.Empty;
        if (raw.Length == 0) return (null, string.Empty);

        // NetBIOS 形式が優先。UPN 形式のアカウント名部に "\" は現れない。
        var slash = raw.LastIndexOf('\\');
        if (slash >= 0)
        {
            var domain = raw[..slash].Trim();
            var account = raw[(slash + 1)..].Trim();
            return (domain.Length == 0 ? null : domain, account);
        }

        var at = raw.IndexOf('@');
        if (at >= 0)
        {
            var account = raw[..at].Trim();
            var domain = raw[(at + 1)..].Trim();
            return (domain.Length == 0 ? null : domain, account);
        }

        return (null, raw);
    }

    /// <summary>照合に使うアカウント名部だけを取り出す。</summary>
    public static string Normalize(string? rawAccountName) => Split(rawAccountName).Account;

    /// <summary>
    /// ドメイン部が設定上許可されているか。
    /// IgnoreDomain ではドメイン部を見ない。AllowList では許可リストとの一致を要求し、
    /// ドメイン部が取れない名前 (ローカルアカウント等) は拒否する。
    /// AllowList を指定しながら許可リストが空の場合も拒否する (設定漏れで素通りさせない)。
    /// </summary>
    public static bool IsDomainAllowed(string? domain, WindowsAuthOptions options)
    {
        if (string.Equals(options.DomainMatch, WindowsAuthDomainMatchModes.IgnoreDomain, StringComparison.OrdinalIgnoreCase))
            return true;

        // 未知の値は設定ミス。別ドメインの同名ユーザーを通さないよう fail-closed にする。
        if (!string.Equals(options.DomainMatch, WindowsAuthDomainMatchModes.AllowList, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(domain)) return false;

        return options.AllowedDomains.Any(d =>
            !string.IsNullOrWhiteSpace(d) &&
            string.Equals(d.Trim(), domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 認証済み OS アカウントが、指定された Watashi ユーザー名の本人であると認めてよいか。
    /// ドメイン許可とアカウント名一致の両方を満たす必要がある。
    /// </summary>
    public static bool Matches(string? rawAccountName, string? watashiUsername, WindowsAuthOptions options)
    {
        var (domain, account) = Split(rawAccountName);
        if (account.Length == 0 || string.IsNullOrWhiteSpace(watashiUsername)) return false;
        if (!IsDomainAllowed(domain, options)) return false;
        return string.Equals(account, watashiUsername.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
