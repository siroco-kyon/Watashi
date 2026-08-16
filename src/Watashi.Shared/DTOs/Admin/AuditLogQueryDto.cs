using Watashi.Shared.Constants;

namespace Watashi.Shared.DTOs.Admin;

/// <summary>監査ログの一覧表示と CSV 出力で共用する検索条件。</summary>
public class AuditLogQueryDto
{
    public string? User { get; set; }
    public string? Op { get; set; }
    public string? Category { get; set; }
    public string? Result { get; set; }
    public string? Host { get; set; }
    public string? Share { get; set; }
    public string? Path { get; set; }
    public string? Node { get; set; }
    public string? Device { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int? Page { get; set; }
}

/// <summary>監査ログのページ応答。表示範囲も同じ計算規則で提供する。</summary>
public class AuditLogPageDto
{
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; }
    public List<AuditLogDto> Items { get; set; } = new();

    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public int FirstItem => TotalCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int LastItem => TotalCount == 0 ? 0 : Math.Min(Page * PageSize, TotalCount);
}

public sealed record AuditLogFilterOptionDto(string Value, string Label);

public sealed record AuditLogOperationOptionDto(string Value, string Label, string Category);

/// <summary>DatePicker のローカル暦日を、サーバーへ送る UTC 境界へ変換する。</summary>
public static class AuditLogDateRange
{
    public static DateTime? LocalDayStartUtc(DateTime? date)
    {
        if (!date.HasValue) return null;
        var localStart = DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Local);
        return localStart.ToUniversalTime();
    }

    public static DateTime? LocalDayEndUtc(DateTime? date)
    {
        if (!date.HasValue) return null;
        // 翌日のローカル 0 時から引くことで、夏時間の切替日も正しい UTC 境界になる。
        var nextLocalStart = DateTime.SpecifyKind(date.Value.Date.AddDays(1), DateTimeKind.Local);
        return nextLocalStart.ToUniversalTime().AddTicks(-1);
    }
}

/// <summary>監査ログ検索 UI とサーバーのカテゴリ判定で共有する値。</summary>
public static class AuditLogFilterValues
{
    public const string FileCategory = "file";
    public const string AuthCategory = "auth";
    public const string AdminCategory = "admin";
    public const string OtherCategory = "other";

    public static IReadOnlyList<string> FileOperationCodes { get; } = new[]
    {
        Operations.List, Operations.Search, Operations.Download, Operations.Upload, Operations.Mkdir,
        Operations.Read, Operations.Write, Operations.Delete, Operations.Rename,
        Operations.Trash, Operations.Restore, Operations.Purge, Operations.Copy,
    };

    public static IReadOnlyList<AuditLogFilterOptionDto> CategoryOptions { get; } = new[]
    {
        new AuditLogFilterOptionDto(string.Empty, "すべて"),
        new AuditLogFilterOptionDto(FileCategory, "ファイル操作"),
        new AuditLogFilterOptionDto(AuthCategory, "認証"),
        new AuditLogFilterOptionDto(AdminCategory, "管理操作"),
        new AuditLogFilterOptionDto(OtherCategory, "その他"),
    };

    public static IReadOnlyList<AuditLogFilterOptionDto> ResultOptions { get; } = new[]
    {
        new AuditLogFilterOptionDto(string.Empty, "すべて"),
        new AuditLogFilterOptionDto(AuditResults.Success, "成功"),
        new AuditLogFilterOptionDto(AuditResults.Failure, "失敗"),
        new AuditLogFilterOptionDto(AuditResults.Warning, "警告"),
    };

    public static IReadOnlyList<AuditLogOperationOptionDto> OperationOptions { get; } = BuildOperationOptions();

    public static string CategoryFor(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation)) return OtherCategory;
        if (FileOperationCodes.Contains(operation, StringComparer.OrdinalIgnoreCase)) return FileCategory;
        if (operation.StartsWith("LOGIN_", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("PASSWORD_", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("REFRESH_", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("TRUSTED_DEVICE_", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals(AuthOperations.Logout, StringComparison.OrdinalIgnoreCase)) return AuthCategory;
        if (operation.StartsWith("ADMIN_", StringComparison.OrdinalIgnoreCase)) return AdminCategory;
        return OtherCategory;
    }

    private static IReadOnlyList<AuditLogOperationOptionDto> BuildOperationOptions()
    {
        string[] operations =
        {
            Operations.List, Operations.Search, Operations.Download, Operations.Upload, Operations.Mkdir,
            Operations.Read, Operations.Write, Operations.Delete, Operations.Rename,
            Operations.Trash, Operations.Restore, Operations.Purge, Operations.Copy,
            AuthOperations.LoginSucceeded, AuthOperations.LoginIdentityMismatch, AuthOperations.LoginDeviceChanged,
            AuthOperations.LoginFailed, AuthOperations.LoginLockedOut,
            AuthOperations.Logout, AuthOperations.RefreshSucceeded, AuthOperations.RefreshReuseRejected,
            AuthOperations.RefreshRejected, AuthOperations.TrustedDeviceRegistered,
            AuthOperations.TrustedDeviceRevoked, AuthOperations.PasswordChanged,
            AuthOperations.PasswordSetupRequested, AuthOperations.PasswordSetupIdentityMismatch,
            AuthOperations.PasswordSetupSucceeded, AuthOperations.PasswordSetupRejected,
            AdminOperations.UserCreate, AdminOperations.UserUpdate, AdminOperations.UserDelete,
            AdminOperations.UserUnlock, AdminOperations.UserDisable, AdminOperations.UserEnable,
            AdminOperations.UserResetPassword, AdminOperations.UserRequireSetup,
            AdminOperations.UserRevokeDevices, AdminOperations.UserImport, AdminOperations.UserExport,
            AdminOperations.HostCreate, AdminOperations.HostUpdate, AdminOperations.HostDelete, AdminOperations.HostTest,
            AdminOperations.ShareCreate, AdminOperations.ShareUpdate, AdminOperations.ShareDelete,
            AdminOperations.TemplateCreate, AdminOperations.TemplateUpdate, AdminOperations.TemplateDelete,
            AdminOperations.PermissionCreate, AdminOperations.PermissionUpdate,
            AdminOperations.PermissionDelete, AdminOperations.PermissionCopy,
            AdminOperations.BundleCreate, AdminOperations.BundleUpdate, AdminOperations.BundleDelete, AdminOperations.BundleApply,
            AdminOperations.NodeCreate, AdminOperations.NodeUpdate, AdminOperations.NodeDelete, AdminOperations.NodeRegenerateKey,
            AdminOperations.SettingUpdate,
        };

        return operations
            .Select(value => new AuditLogOperationOptionDto(
                value, AuditLogDto.FormatOperation(value), CategoryFor(value)))
            .ToArray();
    }
}
