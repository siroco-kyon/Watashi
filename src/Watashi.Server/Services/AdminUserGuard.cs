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

    /// <summary>
    /// 他に「実際にログインできる」管理者が居るかを判定する。
    /// ロック中の管理者に加え、初回パスワード設定待ちの管理者もログイン不能なのでカウントしない。
    /// これを数えてしまうと、実在の管理者を最後の 1 人であるにもかかわらず削除・降格でき、
    /// 管理画面へ誰も入れなくなる。
    /// </summary>
    private static async Task<bool> HasOtherActiveAdminAsync(AppDbContext db, int excludeUserId, CancellationToken ct)
        => await db.Users.AsNoTracking().AnyAsync(
            x => x.IsAdmin && !x.IsLocked && !x.IsPasswordSetupPending && x.Id != excludeUserId, ct);
}
