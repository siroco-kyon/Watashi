namespace Watashi.Shared.Constants;

public static class Operations
{
    public const string Read = "READ";
    public const string Write = "WRITE";
    public const string Delete = "DELETE";
    public const string Rename = "RENAME";
}

public static class AdminOperations
{
    public const string UserCreate = "ADMIN_USER_CREATE";
    public const string UserUpdate = "ADMIN_USER_UPDATE";
    public const string UserDelete = "ADMIN_USER_DELETE";
    public const string UserUnlock = "ADMIN_USER_UNLOCK";
    public const string UserResetPassword = "ADMIN_USER_RESET_PW";
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
    public const string PermissionDelete = "ADMIN_PERMISSION_DELETE";

    public const string NodeCreate = "ADMIN_NODE_CREATE";
    public const string NodeUpdate = "ADMIN_NODE_UPDATE";
    public const string NodeDelete = "ADMIN_NODE_DELETE";
    public const string NodeRegenerateKey = "ADMIN_NODE_REGEN_KEY";

    public const string SettingUpdate = "ADMIN_SETTING_UPDATE";
}
