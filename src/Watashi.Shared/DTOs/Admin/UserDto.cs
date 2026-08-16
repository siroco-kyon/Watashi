namespace Watashi.Shared.DTOs.Admin;

/// <summary>管理画面に見せるパスワードの状態。ハッシュそのものは決して返さない。</summary>
public static class PasswordStatuses
{
    /// <summary>初回設定待ち。本人が Windows 認証で確認されるまでログインできない。</summary>
    public const string PendingSetup = "PendingSetup";
    /// <summary>有効期限切れ。次回ログイン時に変更を強制される。</summary>
    public const string Expired = "Expired";
    /// <summary>管理者リセット直後など、次回ログイン時に変更が必要。</summary>
    public const string MustChange = "MustChange";
    /// <summary>通常。</summary>
    public const string Active = "Active";

    /// <summary>管理画面に出す日本語ラベル。</summary>
    public static string ToLabel(string? status) => status switch
    {
        PendingSetup => "初回設定待ち",
        Expired => "期限切れ",
        MustChange => "要変更",
        Active => "有効",
        _ => status ?? string.Empty,
    };
}

public class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsLocked { get; set; }
    public bool IsDisabled { get; set; }
    public DateTime? DisabledAt { get; set; }
    public string? DisabledReason { get; set; }
    public int? DisabledByUserId { get; set; }
    public string? DisabledByUsername { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime PasswordExpiresAt { get; set; }
    public bool MustChangePassword { get; set; }
    /// <summary><see cref="PasswordStatuses"/> のいずれか。一覧表示はこちらを使う。</summary>
    public string PasswordStatus { get; set; } = PasswordStatuses.Active;
    /// <summary>初回設定の受付期限。null は無期限、または既に設定済み。</summary>
    public DateTime? PasswordSetupExpiresAt { get; set; }
    /// <summary>初回設定時に Windows 認証で確認できた OS アカウント名。</summary>
    public string? WindowsAccountName { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>画面表示用のラベル。</summary>
    public string PasswordStatusLabel => PasswordStatuses.ToLabel(PasswordStatus);

    /// <summary>明示無効化を自動ロックより優先して表示する、管理画面用のアカウント状態。</summary>
    public string AccountStatusLabel => IsDisabled ? "無効" : IsLocked ? "ロック" : "有効";

    /// <summary>
    /// 一覧の「PW期限」列に出す値。初回設定待ちのユーザーはまだパスワードを持たないため、
    /// 期限を出すと「その日に切れる」と誤読される。空欄にする。
    /// </summary>
    public DateTime? PasswordExpiresAtForDisplay =>
        PasswordStatus == PasswordStatuses.PendingSetup ? null : PasswordExpiresAt;
}

/// <summary>
/// ユーザー作成要求。初期パスワードは受け取らない。
/// 作成されたユーザーは初回設定待ちになり、本人が Windows 認証を通してから自分で決める。
/// ドメイン参加していない端末など Windows 認証が使えない場合は、
/// 作成後に管理者が reset-password で初期パスワードを発行する経路を使う。
/// </summary>
public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
}

public class UpdateUserRequest
{
    public bool? IsAdmin { get; set; }
}

/// <summary>管理者による明示的なユーザー無効化。</summary>
public class DisableUserRequest
{
    public const int MaxReasonLength = 500;
    public string Reason { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>CSV インポートの動作モード。</summary>
public static class UserImportModes
{
    /// <summary>既存と同名のユーザーはスキップ。新規のみ追加。</summary>
    public const string AddOnly = "add-only";
    /// <summary>既存ユーザーの IsAdmin も更新する。パスワードには一切触れない。</summary>
    public const string Upsert = "upsert";
}

public class UserImportResultDto
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<UserImportRowError> Errors { get; set; } = new();
    /// <summary>取り込みは成功したが利用者に伝えるべき注意 (旧形式 CSV の Password 列を無視した等)。</summary>
    public List<string> Warnings { get; set; } = new();
}

public class UserImportRowError
{
    public int LineNumber { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
