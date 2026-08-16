using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

/// <summary>
/// 管理者による明示的なユーザー無効化・再有効化の状態変更と、認証情報の即時失効を一括して行う。
/// 呼び出し側が transaction を開始している場合は、その transaction 内で全更新が実行される。
/// </summary>
public static class UserAccountLifecycle
{
    public const int MaxDisableReasonLength = Shared.DTOs.Admin.DisableUserRequest.MaxReasonLength;

    public static async Task DisableAsync(
        AppDbContext db,
        User user,
        int actorUserId,
        string actorUsername,
        string reason,
        DateTime changedAt,
        CancellationToken ct = default)
    {
        var normalizedReason = NormalizeReason(reason);
        user.IsDisabled = true;
        user.DisabledAt = changedAt;
        user.DisabledReason = normalizedReason;
        user.DisabledByUserId = actorUserId;
        user.DisabledByUsername = actorUsername;

        // access token はこの時刻を資格情報世代として持つ。再有効化後も無効化前の token が
        // 復活しないよう、明示無効化時にも世代を進める。
        user.PasswordChangedAt = changedAt;
        await db.SaveChangesAsync(ct);
        await RevokeAuthenticationAsync(db, user.Id, changedAt, "account_disabled", ct);
    }

    public static async Task EnableAsync(
        AppDbContext db,
        User user,
        DateTime changedAt,
        CancellationToken ct = default)
    {
        user.IsDisabled = false;

        // 無効化と同時進行して新しい token/端末が作られた場合でも、再有効化時にもう一度世代更新と
        // 全失効を行えば古い認証情報は残らない。無効理由・日時・操作者は直近証跡として保持する。
        user.PasswordChangedAt = changedAt;
        await db.SaveChangesAsync(ct);
        await RevokeAuthenticationAsync(db, user.Id, changedAt, "account_reenabled", ct);
    }

    private static async Task RevokeAuthenticationAsync(
        AppDbContext db,
        int userId,
        DateTime changedAt,
        string deviceReason,
        CancellationToken ct)
    {
        await db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsRevoked, true)
                .SetProperty(t => t.LastUsedAt, changedAt), ct);

        await db.TrustedDevices
            .Where(d => d.UserId == userId && !d.IsRevoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.IsRevoked, true)
                .SetProperty(d => d.RevokedAt, changedAt)
                .SetProperty(d => d.RevokedReason, deviceReason), ct);
    }

    private static string NormalizeReason(string reason)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("無効理由は必須です。", nameof(reason));
        if (normalized.Length > MaxDisableReasonLength)
            throw new ArgumentException($"無効理由は {MaxDisableReasonLength} 文字以内で入力してください。", nameof(reason));
        return normalized;
    }
}
