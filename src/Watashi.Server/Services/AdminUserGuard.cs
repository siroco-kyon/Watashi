using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;

namespace Watashi.Server.Services;

/// <summary>
/// 管理者ユーザーの危険操作（自己削除・最後の管理者削除・自己降格・最後の管理者降格）を
/// 拒否するためのガード。エンドポイントから抽出してユニットテスト可能にしている。
/// </summary>
public static class AdminUserGuard
{
    public enum Decision
    {
        Allow,
        SelfTarget,
        LastActiveAdmin,
    }

    /// <summary>削除可否を判定する。</summary>
    /// <param name="db">DbContext (読み取り専用に使う)</param>
    /// <param name="actorUserId">操作実施者の UserId (匿名なら null)</param>
    /// <param name="targetUserId">削除対象 UserId</param>
    /// <param name="targetIsAdmin">削除対象が IsAdmin か</param>
    public static async Task<Decision> CanDeleteAsync(
        AppDbContext db, int? actorUserId, int targetUserId, bool targetIsAdmin, CancellationToken ct = default)
    {
        if (actorUserId.HasValue && actorUserId.Value == targetUserId)
            return Decision.SelfTarget;
        if (targetIsAdmin && !await HasOtherActiveAdminAsync(db, targetUserId, ct))
            return Decision.LastActiveAdmin;
        return Decision.Allow;
    }

    /// <summary>管理者フラグの変更可否を判定する。IsAdmin を true→false にするときだけ防御。</summary>
    public static async Task<Decision> CanDemoteAsync(
        AppDbContext db, int? actorUserId, int targetUserId, bool currentIsAdmin, bool newIsAdmin,
        CancellationToken ct = default)
    {
        if (!currentIsAdmin || newIsAdmin) return Decision.Allow;
        if (actorUserId.HasValue && actorUserId.Value == targetUserId)
            return Decision.SelfTarget;
        if (!await HasOtherActiveAdminAsync(db, targetUserId, ct))
            return Decision.LastActiveAdmin;
        return Decision.Allow;
    }

    private static async Task<bool> HasOtherActiveAdminAsync(AppDbContext db, int excludeUserId, CancellationToken ct)
        => await db.Users.AsNoTracking().AnyAsync(x => x.IsAdmin && !x.IsLocked && x.Id != excludeUserId, ct);
}
