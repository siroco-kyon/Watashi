namespace Watashi.Shared.Constants;

public static class Operations
{
    public const string Read = "READ";
    public const string Write = "WRITE";
    public const string Delete = "DELETE";
    public const string Rename = "RENAME";
    public const string List = "LIST";
    public const string Download = "DOWNLOAD";
    public const string Upload = "UPLOAD";
    public const string Mkdir = "MKDIR";
    public const string Search = "SEARCH";
    public const string Trash = "TRASH";
    public const string Restore = "RESTORE";
    public const string Purge = "PURGE";
    public const string Copy = "COPY";
}

public static class AuthOperations
{
    /// <summary>通常ログインまたは信頼済み端末ログインが成功した。</summary>
    public const string LoginSucceeded = "LOGIN_SUCCEEDED";
    /// <summary>Windows ログオンユーザー名と Watashi ログインユーザー名が異なる状態でのログイン (ログイン自体は許可)。</summary>
    public const string LoginIdentityMismatch = "LOGIN_IDENTITY_MISMATCH";
    /// <summary>前回ログイン時と異なる端末 (マシン名) からのログイン。</summary>
    public const string LoginDeviceChanged = "LOGIN_DEVICE_CHANGED";
    /// <summary>ログイン失敗 (パスワード不一致 / 存在しないユーザー / ロック中アカウントへの試行)。</summary>
    public const string LoginFailed = "LOGIN_FAILED";
    /// <summary>連続ログイン失敗によりアカウントがロックされた。</summary>
    public const string LoginLockedOut = "LOGIN_LOCKED_OUT";
    /// <summary>ログアウトにより refresh token を失効させた、または失効要求を拒否した。</summary>
    public const string Logout = "LOGOUT";
    /// <summary>refresh token のローテーションが成功した。</summary>
    public const string RefreshSucceeded = "REFRESH_SUCCEEDED";
    /// <summary>refresh token の再利用を検出して token family を拒否・失効した。</summary>
    public const string RefreshReuseRejected = "REFRESH_REUSE_REJECTED";
    /// <summary>再利用以外の理由で refresh を拒否した。</summary>
    public const string RefreshRejected = "REFRESH_REJECTED";
    /// <summary>信頼済み端末を登録した。</summary>
    public const string TrustedDeviceRegistered = "TRUSTED_DEVICE_REGISTERED";
    /// <summary>信頼済み端末を失効または置換した。</summary>
    public const string TrustedDeviceRevoked = "TRUSTED_DEVICE_REVOKED";
    /// <summary>本人による通常パスワード変更が成功または拒否された。</summary>
    public const string PasswordChanged = "PASSWORD_CHANGED";
    /// <summary>初回パスワード設定の要求 (本人確認に成功し、設定画面を返した)。</summary>
    public const string PasswordSetupRequested = "PASSWORD_SETUP_REQUESTED";
    /// <summary>初回パスワード設定で、Windows 認証済みの OS ユーザーと対象 Watashi ユーザーが一致しなかった。</summary>
    public const string PasswordSetupIdentityMismatch = "PASSWORD_SETUP_IDENTITY_MISMATCH";
    /// <summary>初回パスワード設定が完了した。</summary>
    public const string PasswordSetupSucceeded = "PASSWORD_SETUP_SUCCEEDED";
    /// <summary>初回パスワード設定を拒否した (期限切れ / 設定済み / ポリシー違反 / 二重送信)。</summary>
    public const string PasswordSetupRejected = "PASSWORD_SETUP_REJECTED";
}

public static class AdminOperations
{
    public const string UserCreate = "ADMIN_USER_CREATE";
    public const string UserUpdate = "ADMIN_USER_UPDATE";
    public const string UserDelete = "ADMIN_USER_DELETE";
    public const string UserUnlock = "ADMIN_USER_UNLOCK";
    public const string UserDisable = "ADMIN_USER_DISABLE";
    public const string UserEnable = "ADMIN_USER_ENABLE";
    public const string UserResetPassword = "ADMIN_USER_RESET_PW";
    /// <summary>ユーザーを初回パスワード設定待ちへ戻した。</summary>
    public const string UserRequireSetup = "ADMIN_USER_REQUIRE_SETUP";
    public const string UserRevokeDevices = "ADMIN_USER_REVOKE_DEVICES";
    public const string UserImport = "ADMIN_USER_IMPORT";
    public const string UserExport = "ADMIN_USER_EXPORT";

    public const string HostCreate = "ADMIN_HOST_CREATE";
    public const string HostUpdate = "ADMIN_HOST_UPDATE";
    public const string HostDelete = "ADMIN_HOST_DELETE";
    public const string HostTest = "ADMIN_HOST_TEST";

    public const string ShareCreate = "ADMIN_SHARE_CREATE";
    public const string ShareUpdate = "ADMIN_SHARE_UPDATE";
    public const string ShareDelete = "ADMIN_SHARE_DELETE";

    public const string TemplateCreate = "ADMIN_TEMPLATE_CREATE";
    public const string TemplateUpdate = "ADMIN_TEMPLATE_UPDATE";
    public const string TemplateDelete = "ADMIN_TEMPLATE_DELETE";

    public const string PermissionCreate = "ADMIN_PERMISSION_CREATE";
    public const string PermissionUpdate = "ADMIN_PERMISSION_UPDATE";
    public const string PermissionDelete = "ADMIN_PERMISSION_DELETE";
    public const string PermissionCopy = "ADMIN_PERMISSION_COPY";

    public const string BundleCreate = "ADMIN_BUNDLE_CREATE";
    public const string BundleUpdate = "ADMIN_BUNDLE_UPDATE";
    public const string BundleDelete = "ADMIN_BUNDLE_DELETE";
    public const string BundleApply = "ADMIN_BUNDLE_APPLY";

    public const string NodeCreate = "ADMIN_NODE_CREATE";
    public const string NodeUpdate = "ADMIN_NODE_UPDATE";
    public const string NodeDelete = "ADMIN_NODE_DELETE";
    public const string NodeRegenerateKey = "ADMIN_NODE_REGEN_KEY";

    public const string SettingUpdate = "ADMIN_SETTING_UPDATE";
}
