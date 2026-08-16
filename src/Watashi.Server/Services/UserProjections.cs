using System.Linq.Expressions;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

/// <summary>
/// User → 管理画面向け DTO の射影。一覧と CSV エクスポートで同じ式を使う。
///
/// パスワード状態の判定順序 (未設定 → 期限切れ → 要変更 → 有効) を 1 箇所に閉じ込めるための型。
/// この式は SQL に変換されて実行される。DateTime 列は値コンバータで ISO-8601 (o 書式) の
/// TEXT として保存されているため、期限比較は文字列の辞書順比較になる。o 書式は桁数が固定で
/// 辞書順 = 時系列順になるので正しく動く (AuditLogPurgeService の Timestamp 比較と同じ前提)。
/// </summary>
public static class UserProjections
{
    public static Expression<Func<User, UserDto>> ToDto(DateTime now) => u => new UserDto
    {
        Id = u.Id,
        Username = u.Username,
        IsAdmin = u.IsAdmin,
        IsLocked = u.IsLocked,
        IsDisabled = u.IsDisabled,
        DisabledAt = u.DisabledAt,
        DisabledReason = u.DisabledReason,
        DisabledByUserId = u.DisabledByUserId,
        DisabledByUsername = u.DisabledByUsername,
        LastLoginAt = u.LastLoginAt,
        PasswordExpiresAt = u.PasswordExpiresAt,
        MustChangePassword = u.MustChangePassword,
        PasswordStatus =
            u.IsPasswordSetupPending ? PasswordStatuses.PendingSetup
            : u.PasswordExpiresAt <= now ? PasswordStatuses.Expired
            : u.MustChangePassword ? PasswordStatuses.MustChange
            : PasswordStatuses.Active,
        PasswordSetupExpiresAt = u.PasswordSetupExpiresAt,
        WindowsAccountName = u.WindowsAccountName,
        CreatedAt = u.CreatedAt,
    };
}
