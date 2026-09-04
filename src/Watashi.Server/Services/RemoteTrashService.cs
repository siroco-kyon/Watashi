using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

public sealed class RemoteTrashException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public RemoteTrashException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }
}

public sealed record RemoteTrashActionResult(
    RemoteTrashEntryDto Entry,
    int ExecutionNodeId,
    int? PermissionId,
    string SourcePath,
    string? TargetPath,
    bool Changed);

/// <summary>
/// リモートごみ箱の永続状態機械。共有単位のlockで容量判定とrenameを直列化し、
/// SMB rename後にHTTP応答またはDB更新が失われても物理状態から再調整できる。
/// </summary>
public sealed class RemoteTrashService
{
    internal const int DefaultRetentionDays = 30;
    /// <summary>保持期間を過ぎてもなお物理削除できない項目を諦めるまでの猶予。</summary>
    internal static readonly TimeSpan CleanupGiveUpAfter = TimeSpan.FromDays(7);
    internal const long DefaultCapacityBytes = 100L * 1024 * 1024 * 1024;
    private static readonly KeyedAsyncLock<Guid> EntryLocks = new();

    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;
    private readonly NodeRouter _router;
    private readonly EncryptionService _encryption;
    private readonly AuditLogService _audit;
    private readonly ILogger<RemoteTrashService> _logger;
    private readonly TimeProvider _clock;

    public RemoteTrashService(
        AppDbContext db,
        PermissionService permissions,
        NodeRouter router,
        EncryptionService encryption,
        AuditLogService audit,
        ILogger<RemoteTrashService> logger,
        TimeProvider clock)
    {
        _db = db;
        _permissions = permissions;
        _router = router;
        _encryption = encryption;
        _audit = audit;
        _logger = logger;
        _clock = clock;
    }

    public async Task<RemoteTrashActionResult> TrashAsync(
        int userId,
        string username,
        int hostId,
        int shareId,
        string sourcePath,
        ExecutionNode node,
        CifsConnectionInfo info,
        int? permissionId,
        CancellationToken ct)
    {
        if (userId <= 0) throw Error(StatusCodes.Status401Unauthorized, "unauthorized", "認証が必要です。");
        var source = NormalizeUserPath(sourcePath);
        var shareGate = await DurableShareLock.AcquireAsync(shareId, ct);
        try
        {
            // DELETE応答だけが失われた再送は、sourceが消えていて同じ利用者のactive entryが
            // 残っている場合に限って同じ結果を返す。sourceが再作成済みなら新規削除として扱う。
            var sourceMetadata = await _router.GetTransferMetadataAsync(node, info, source, ct);
            if (!sourceMetadata.Exists)
            {
                var recovered = await FindLatestSourceEntryAsync(userId, hostId, shareId, source, ct);
                if (recovered is not null && await RecoverTrashingAsync(recovered, node, info, ct))
                    return Result(recovered, node.Id, permissionId, recovered.OriginalPath, recovered.TrashPath, changed: false);
                throw Error(StatusCodes.Status404NotFound, "source_not_found", "削除対象が見つかりません。");
            }

            RemoteTrashItemMetadata inspected;
            try
            {
                inspected = await _router.InspectForTrashAsync(node, info, source, ct);
            }
            catch (TransferReparsePointException ex)
            {
                throw Error(StatusCodes.Status409Conflict, "reparse_point_rejected", ex.Message);
            }

            var settings = await GetSettingsAsync(ct);
            var usedBytes = await _db.RemoteTrashEntries.AsNoTracking()
                .Where(e => e.ShareId == shareId &&
                    (e.Status == RemoteTrashStatuses.Trashing ||
                     e.Status == RemoteTrashStatuses.Active ||
                     e.Status == RemoteTrashStatuses.Restoring ||
                     e.Status == RemoteTrashStatuses.Purging))
                .SumAsync(e => (long?)e.SizeBytes, ct) ?? 0;
            if (settings.CapacityBytes > 0 &&
                (usedBytes >= settings.CapacityBytes || inspected.SizeBytes > settings.CapacityBytes - usedBytes))
            {
                throw Error(StatusCodes.Status507InsufficientStorage, "trash_capacity_exceeded",
                    "リモートごみ箱の容量上限を超えるため削除できません。管理者にごみ箱の整理を依頼してください。");
            }

            var now = UtcNow();
            var entity = new RemoteTrashEntry
            {
                Id = Guid.NewGuid(),
                DeletedByUserId = userId,
                DeletedByUsername = string.IsNullOrWhiteSpace(username) ? $"user:{userId}" : username.Trim(),
                HostId = hostId,
                ShareId = shareId,
                OriginalPath = source,
                ItemType = inspected.Type,
                SizeBytes = inspected.SizeBytes,
                OriginalModifiedAtUtc = inspected.ModifiedAtUtc?.ToUniversalTime(),
                Status = RemoteTrashStatuses.Trashing,
                DeletedAt = now,
                ExpiresAt = now.AddDays(settings.RetentionDays),
                UpdatedAt = now,
            };
            entity.TrashPath = RemoteTrashPathPolicy.BuildItemPath(entity.Id);
            _db.RemoteTrashEntries.Add(entity);
            await _db.SaveChangesAsync(ct);

            try
            {
                await _router.MoveToTrashAsync(node, info, source, entity.TrashPath, ct);
                entity.Status = RemoteTrashStatuses.Active;
                entity.ErrorCode = null;
                entity.UpdatedAt = UtcNow();
                // renameが完了した後はクライアント切断で台帳更新を取り消さない。
                await _db.SaveChangesAsync(CancellationToken.None);
                return Result(entity, node.Id, permissionId, source, entity.TrashPath, changed: true);
            }
            catch
            {
                await MarkTrashFailureOrRecoveryAsync(entity, node, info);
                throw;
            }
        }
        finally
        {
            shareGate.Dispose();
        }
    }

    public async Task<RemoteTrashListResponse> ListAsync(
        int userId,
        bool isAdmin,
        int? hostId,
        int? shareId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        if (userId <= 0) throw Error(StatusCodes.Status401Unauthorized, "unauthorized", "認証が必要です。");
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.RemoteTrashEntries.AsNoTracking()
            .Where(e => e.Status == RemoteTrashStatuses.Active);
        if (!isAdmin)
            query = query.Where(e => e.DeletedByUserId == userId);
        if (hostId.HasValue)
            query = query.Where(e => e.HostId == hostId.Value);
        if (shareId.HasValue)
            query = query.Where(e => e.ShareId == shareId.Value);

        var totalCount = await query.CountAsync(ct);
        var totalBytes = await query.SumAsync(e => (long?)e.SizeBytes, ct) ?? 0;
        var entries = await query.OrderByDescending(e => e.DeletedAt)
            .ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new RemoteTrashListResponse
        {
            Items = entries.Select(ToDto).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalBytes = totalBytes,
        };
    }

    public async Task<RemoteTrashActionResult> RestoreAsync(
        int userId,
        bool isAdmin,
        Guid entryId,
        string? collisionPolicy,
        CancellationToken ct)
    {
        string policy;
        try { policy = TrashCollisionPolicies.Normalize(collisionPolicy); }
        catch (ArgumentException ex) { throw Error(StatusCodes.Status400BadRequest, "invalid_collision_policy", ex.Message); }
        if (policy == TrashCollisionPolicies.Overwrite && !isAdmin)
            throw Error(StatusCodes.Status403Forbidden, "overwrite_requires_admin",
                "既存項目を完全に置換する復元は管理者だけが実行できます。");

        var entryGate = await EntryLocks.AcquireAsync(entryId, ct);
        try
        {
            var entry = await FindVisibleEntryAsync(userId, isAdmin, entryId, ct);
            if (entry.Status == RemoteTrashStatuses.Restored)
                return Result(entry, 0, null, entry.TrashPath, entry.RestoredPath, changed: false);
            if (entry.Status == RemoteTrashStatuses.Purged)
                throw Error(StatusCodes.Status410Gone, "entry_purged", "この項目は既に完全削除されています。");
            if (entry.Status is not (RemoteTrashStatuses.Active or RemoteTrashStatuses.Restoring))
                throw Error(StatusCodes.Status409Conflict, "entry_not_restorable", "この項目は現在復元できません。");

            var shareGate = await DurableShareLock.AcquireAsync(entry.ShareId, ct);
            try
            {
                var execution = await ResolveExecutionAsync(entry.HostId, entry.ShareId, ct);
                if (entry.Status == RemoteTrashStatuses.Restoring &&
                    await TryRecoverRestoreAsync(entry, execution, ct))
                    return Result(entry, execution.Node.Id, null, entry.TrashPath, entry.RestoredPath, changed: false);

                var target = entry.OriginalPath;
                int? permissionId = null;
                if (!isAdmin)
                {
                    // 復元先の存在有無を返す前に権限を再評価し、削除後に権限を失った
                    // 利用者へ同名ファイルの存在を漏らさない。
                    var originalPermission = await _permissions.CanPerformAsync(
                        userId, entry.ShareId, target, Operations.Write, ct);
                    if (!originalPermission.allowed)
                        throw Error(StatusCodes.Status403Forbidden, "restore_permission_denied",
                            "復元先への書き込み権限がありません。");
                    permissionId = originalPermission.permissionId;
                }
                var existing = await _router.GetTransferMetadataAsync(
                    execution.Node, execution.Info, target, ct);
                var replace = false;
                if (existing.Exists)
                {
                    if (existing.IsReparsePoint)
                        throw Error(StatusCodes.Status409Conflict, "reparse_point_rejected",
                            "復元先のリパースポイントは置換できません。");
                    switch (policy)
                    {
                        case TrashCollisionPolicies.Fail:
                            throw Error(StatusCodes.Status409Conflict, "target_exists",
                                "元の場所に同名の項目があります。衝突ポリシーを選択してください。");
                        case TrashCollisionPolicies.Rename:
                            target = await FindAvailableRestorePathAsync(entry, execution, userId, isAdmin, ct);
                            break;
                        case TrashCollisionPolicies.Overwrite:
                            replace = true;
                            break;
                    }
                }

                if (!isAdmin)
                {
                    var permission = await _permissions.CanPerformAsync(
                        userId, entry.ShareId, target, Operations.Write, ct);
                    if (!permission.allowed)
                        throw Error(StatusCodes.Status403Forbidden, "restore_permission_denied",
                            "復元先への書き込み権限がありません。");
                    permissionId = permission.permissionId;
                }

                entry.Status = RemoteTrashStatuses.Restoring;
                entry.RestoredPath = target;
                entry.ErrorCode = null;
                entry.UpdatedAt = UtcNow();
                await _db.SaveChangesAsync(ct);
                try
                {
                    await _router.RestoreFromTrashAsync(
                        execution.Node, execution.Info, entry.TrashPath, target, replace, ct);
                    entry.Status = RemoteTrashStatuses.Restored;
                    entry.RestoredAt = UtcNow();
                    entry.RestoredByUserId = userId;
                    entry.UpdatedAt = entry.RestoredAt.Value;
                    await _db.SaveChangesAsync(CancellationToken.None);
                    return Result(entry, execution.Node.Id, permissionId, entry.TrashPath, target, changed: true);
                }
                catch
                {
                    if (!await TryRecoverRestoreAsync(entry, execution, CancellationToken.None))
                    {
                        entry.Status = RemoteTrashStatuses.Active;
                        entry.ErrorCode = "restore_failed";
                        entry.UpdatedAt = UtcNow();
                        await TrySaveAsync();
                    }
                    throw;
                }
            }
            finally
            {
                shareGate.Dispose();
            }
        }
        finally
        {
            entryGate.Dispose();
        }
    }

    /// <summary>即時完全削除。通常利用者には権限を付与せず、管理者の明示操作だけを許可する。</summary>
    public async Task<RemoteTrashActionResult> PurgeAsync(
        int userId,
        bool isAdmin,
        Guid entryId,
        CancellationToken ct)
    {
        if (!isAdmin)
            throw Error(StatusCodes.Status403Forbidden, "purge_requires_admin",
                "即時完全削除は管理者だけが実行できます。");
        var entryGate = await EntryLocks.AcquireAsync(entryId, ct);
        try
        {
            var entry = await FindVisibleEntryAsync(userId, isAdmin: true, entryId, ct);
            var result = await PurgeCoreAsync(entry, userId, ct);
            return result;
        }
        finally
        {
            entryGate.Dispose();
        }
    }

    /// <summary>期限切れ項目を最大100件purgeし、system actorの監査を残す。</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var now = UtcNow();
        var ids = await _db.RemoteTrashEntries.AsNoTracking()
            .Where(e => (e.Status == RemoteTrashStatuses.Active ||
                         e.Status == RemoteTrashStatuses.Purging ||
                         e.Status == RemoteTrashStatuses.Failed) &&
                        e.ExpiresAt <= now)
            .OrderBy(e => e.ExpiresAt)
            .Select(e => e.Id)
            .Take(100)
            .ToListAsync(ct);
        var count = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var gate = await EntryLocks.AcquireAsync(id, ct);
            try
            {
                var entry = await _db.RemoteTrashEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
                if (entry is null || entry.ExpiresAt > UtcNow() ||
                    entry.Status is not (RemoteTrashStatuses.Active or
                        RemoteTrashStatuses.Purging or RemoteTrashStatuses.Failed))
                    continue;
                try
                {
                    if (entry.Status == RemoteTrashStatuses.Failed)
                    {
                        // 廃止前の失敗行も、物理trashが無ければidempotent purgeでPurgedへ
                        // 収束できる。APIは再公開せずjanitorだけでlegacy状態を回収する。
                        entry.Status = RemoteTrashStatuses.Active;
                        entry.ErrorCode = "legacy_cleanup";
                        entry.UpdatedAt = UtcNow();
                        await _db.SaveChangesAsync(ct);
                    }
                    var result = await PurgeCoreAsync(entry, actorUserId: null, ct);
                    if (result.Changed) count++;
                    try
                    {
                        await _audit.LogAsync(new AuditLog
                        {
                            Timestamp = UtcNow(),
                            Username = "(system)",
                            Operation = Operations.Purge,
                            HostId = entry.HostId,
                            ShareId = entry.ShareId,
                            Path = entry.TrashPath,
                            TargetPath = null,
                            Result = AuditResults.Success,
                            BytesTransferred = entry.SizeBytes,
                            ExecutionNodeId = result.ExecutionNodeId,
                        }, CancellationToken.None);
                    }
                    catch (Exception auditError)
                    {
                        _logger.LogError(auditError, "期限切れごみ箱項目 {EntryId} のPURGE監査に失敗しました", entry.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "期限切れごみ箱項目 {EntryId} のpurgeに失敗しました", id);
                    await DeferFailedPurgeAsync(id, ct);
                }
            }
            finally
            {
                gate.Dispose();
            }
        }
        return count;
    }

    /// <summary>
    /// 共有の廃止・付け替えのために、その共有に残るごみ箱台帳を管理者権限で終端させる。
    /// まず正規の完全削除を試し、到達できないものだけ台帳を諦めて残骸のパスを返す。
    /// 列挙中に新しい台帳が生まれると取りこぼすため、呼び出し側が DurableShareLock を
    /// 保持したまま呼ぶこと。PurgeCoreAsync には保持済みであることを伝えて二重取得を避ける。
    /// </summary>
    public async Task<(int CleanedUp, int Abandoned, List<string> OrphanedPaths)> ReleaseForShareAsync(
        int shareId,
        int actorUserId,
        CancellationToken ct)
    {
        var ids = await _db.RemoteTrashEntries.AsNoTracking()
            .Where(e => e.ShareId == shareId &&
                        e.Status != RemoteTrashStatuses.Purged &&
                        e.Status != RemoteTrashStatuses.Restored)
            .OrderBy(e => e.DeletedAt)
            .Select(e => e.Id)
            .ToListAsync(ct);

        var cleanedUp = 0;
        var abandoned = 0;
        var orphaned = new List<string>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var gate = await EntryLocks.AcquireAsync(id, ct);
            try
            {
                var entry = await _db.RemoteTrashEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
                if (entry is null || entry.Status is RemoteTrashStatuses.Purged or RemoteTrashStatuses.Restored)
                    continue;

                try
                {
                    // Trashing / Restoring / Failed は PurgeCore が受け付けないため、
                    // janitor と同じく Active へ寄せてから idempotent な purge を通す。
                    if (entry.Status is not (RemoteTrashStatuses.Active or RemoteTrashStatuses.Purging))
                    {
                        entry.Status = RemoteTrashStatuses.Active;
                        entry.UpdatedAt = UtcNow();
                        await _db.SaveChangesAsync(ct);
                    }
                    await PurgeCoreAsync(entry, actorUserId, ct, shareLockHeld: true);
                    cleanedUp++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "共有 {ShareId} のごみ箱項目 {EntryId} は実体を回収できないまま終端させます", shareId, id);
                    _db.ChangeTracker.Clear();
                    var stale = await _db.RemoteTrashEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
                    if (stale is null) continue;
                    stale.Status = RemoteTrashStatuses.Purged;
                    stale.ErrorCode = TransferCleanupErrorCodes.AdminAbandoned;
                    stale.PurgedAt = UtcNow();
                    stale.PurgedByUserId = actorUserId;
                    stale.UpdatedAt = stale.PurgedAt.Value;
                    await _db.SaveChangesAsync(ct);
                    orphaned.Add(stale.TrashPath);
                    abandoned++;
                }
            }
            finally
            {
                gate.Dispose();
            }
        }
        return (cleanedUp, abandoned, orphaned);
    }

    private async Task DeferFailedPurgeAsync(Guid entryId, CancellationToken ct)
    {
        try
        {
            // PurgeCore内のmetadata再確認まで失敗するとPurgingのまま残り得る。
            // 再実行は物理項目が既に無くてもidempotentなのでActiveへ戻し、先頭100件が
            // 毎回同じ失敗項目で固定されないよう次回試行を将来へ送る。
            var entry = await _db.RemoteTrashEntries.FirstOrDefaultAsync(e => e.Id == entryId, ct);
            if (entry is null || entry.Status == RemoteTrashStatuses.Purged)
                return;

            // 共有が到達不能になると物理削除は二度と成功しない。無期限の再試行は死んだホストへの
            // 接続試行とログを出し続け、共有の削除・付け替えも永久にブロックする。
            // 削除時刻から保持期間 + 猶予を過ぎたものは諦めて終端させる。
            var settings = await GetSettingsAsync(ct);
            var giveUpAt = entry.DeletedAt.AddDays(settings.RetentionDays) + CleanupGiveUpAfter;
            if (UtcNow() > giveUpAt)
            {
                _logger.LogWarning(
                    "ごみ箱項目 {EntryId} の実体を回収できないまま諦めます。" +
                    "共有上に {TrashPath} が残っている可能性があります",
                    entryId, entry.TrashPath);
                entry.Status = RemoteTrashStatuses.Purged;
                entry.ErrorCode = TransferCleanupErrorCodes.CleanupGaveUp;
                entry.PurgedAt = UtcNow();
                entry.UpdatedAt = entry.PurgedAt.Value;
                await _db.SaveChangesAsync(ct);
                return;
            }

            entry.Status = RemoteTrashStatuses.Active;
            entry.ErrorCode = "purge_retry";
            entry.UpdatedAt = UtcNow();
            entry.ExpiresAt = UtcNow().AddMinutes(5);
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception retryError)
        {
            _logger.LogWarning(retryError,
                "期限切れごみ箱項目 {EntryId} のpurge再試行延期を保存できませんでした", entryId);
            _db.ChangeTracker.Clear();
        }
    }

    /// <param name="shareLockHeld">
    /// 呼び出し側が既に DurableShareLock を保持している場合は true。KeyedAsyncLock は
    /// 再入可能ではないため、ここで取り直すと自分自身と競合して停止する。
    /// </param>
    private async Task<RemoteTrashActionResult> PurgeCoreAsync(
        RemoteTrashEntry entry,
        int? actorUserId,
        CancellationToken ct,
        bool shareLockHeld = false)
    {
        if (entry.Status == RemoteTrashStatuses.Purged)
            return Result(entry, 0, null, entry.TrashPath, null, changed: false);
        if (entry.Status == RemoteTrashStatuses.Restored)
            throw Error(StatusCodes.Status409Conflict, "entry_restored", "復元済み項目は完全削除できません。");
        if (entry.Status is not (RemoteTrashStatuses.Active or RemoteTrashStatuses.Purging))
            throw Error(StatusCodes.Status409Conflict, "entry_not_purgeable", "この項目は現在完全削除できません。");

        var shareGate = shareLockHeld
            ? null
            : await DurableShareLock.AcquireAsync(entry.ShareId, ct);
        try
        {
            var execution = await ResolveExecutionAsync(entry.HostId, entry.ShareId, ct);
            entry.Status = RemoteTrashStatuses.Purging;
            entry.ErrorCode = null;
            entry.UpdatedAt = UtcNow();
            await _db.SaveChangesAsync(ct);
            try
            {
                await _router.PurgeTrashItemAsync(
                    execution.Node, execution.Info, entry.TrashPath, ct);
                entry.Status = RemoteTrashStatuses.Purged;
                entry.PurgedAt = UtcNow();
                entry.PurgedByUserId = actorUserId;
                entry.UpdatedAt = entry.PurgedAt.Value;
                await _db.SaveChangesAsync(CancellationToken.None);
                return Result(entry, execution.Node.Id, null, entry.TrashPath, null, changed: true);
            }
            catch
            {
                var metadata = await TryGetMetadataAsync(execution, entry.TrashPath);
                if (metadata is { Exists: false })
                {
                    entry.Status = RemoteTrashStatuses.Purged;
                    entry.PurgedAt = UtcNow();
                    entry.PurgedByUserId = actorUserId;
                }
                else
                {
                    entry.Status = RemoteTrashStatuses.Active;
                    entry.ErrorCode = "purge_failed";
                }
                entry.UpdatedAt = UtcNow();
                await TrySaveAsync();
                throw;
            }
        }
        finally
        {
            shareGate?.Dispose();
        }
    }

    private async Task<string> FindAvailableRestorePathAsync(
        RemoteTrashEntry entry,
        ResolvedExecution execution,
        int userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var original = entry.OriginalPath;
        var parent = Watashi.Shared.Helpers.PathHelper.GetParent(original);
        var name = original[(original.LastIndexOf('/') + 1)..];
        var extensionIndex = entry.ItemType == TransferFileTypes.File
            ? name.LastIndexOf('.')
            : -1;
        var stem = extensionIndex > 0 ? name[..extensionIndex] : name;
        var extension = extensionIndex > 0 ? name[extensionIndex..] : string.Empty;
        for (var i = 1; i <= 1000; i++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = Watashi.Shared.Helpers.PathHelper.NormalizePath(
                $"{parent}/{stem} (restored {i}){extension}");
            if (candidate.Length > RemoteTrashPathPolicy.MaxPathChars) break;
            var metadata = await _router.GetTransferMetadataAsync(
                execution.Node, execution.Info, candidate, ct);
            if (metadata.Exists)
            {
                if (metadata.IsReparsePoint)
                    throw Error(StatusCodes.Status409Conflict, "reparse_point_rejected",
                        "復元候補にリパースポイントがあります。");
                continue;
            }
            if (!isAdmin)
            {
                var allowed = await _permissions.CanPerformAsync(
                    userId, entry.ShareId, candidate, Operations.Write, ct);
                if (!allowed.allowed) continue;
            }
            return candidate;
        }
        throw Error(StatusCodes.Status409Conflict, "restore_name_exhausted",
            "衝突しない復元名を作成できませんでした。");
    }

    private async Task<RemoteTrashEntry?> FindLatestSourceEntryAsync(
        int userId,
        int hostId,
        int shareId,
        string source,
        CancellationToken ct)
        => await _db.RemoteTrashEntries
            .Where(e => e.DeletedByUserId == userId && e.HostId == hostId && e.ShareId == shareId &&
                        e.OriginalPath.ToLower() == source.ToLower() &&
                        (e.Status == RemoteTrashStatuses.Trashing || e.Status == RemoteTrashStatuses.Active))
            .OrderByDescending(e => e.DeletedAt)
            .FirstOrDefaultAsync(ct);

    private async Task<bool> RecoverTrashingAsync(
        RemoteTrashEntry entry,
        ExecutionNode node,
        CifsConnectionInfo info,
        CancellationToken ct)
    {
        if (entry.Status == RemoteTrashStatuses.Active) return true;
        var trash = await _router.GetTransferMetadataAsync(node, info, entry.TrashPath, ct);
        if (!trash.Exists) return false;
        if (trash.IsReparsePoint) throw Error(StatusCodes.Status409Conflict, "reparse_point_rejected", "ごみ箱項目がリパースポイントです。");
        entry.Status = RemoteTrashStatuses.Active;
        entry.ErrorCode = null;
        entry.UpdatedAt = UtcNow();
        await _db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    private async Task MarkTrashFailureOrRecoveryAsync(
        RemoteTrashEntry entry,
        ExecutionNode node,
        CifsConnectionInfo info)
    {
        var trash = await TryGetMetadataAsync(new ResolvedExecution(info, node), entry.TrashPath);
        if (trash is { Exists: true, IsReparsePoint: false })
        {
            entry.Status = RemoteTrashStatuses.Active;
            entry.ErrorCode = null;
        }
        else
        {
            entry.Status = RemoteTrashStatuses.Failed;
            entry.ErrorCode = "trash_failed";
        }
        entry.UpdatedAt = UtcNow();
        await TrySaveAsync();
    }

    private async Task<bool> TryRecoverRestoreAsync(
        RemoteTrashEntry entry,
        ResolvedExecution execution,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.RestoredPath)) return false;
        var trash = await _router.GetTransferMetadataAsync(
            execution.Node, execution.Info, entry.TrashPath, ct);
        var target = await _router.GetTransferMetadataAsync(
            execution.Node, execution.Info, entry.RestoredPath, ct);
        if (trash.Exists || !target.Exists || target.IsReparsePoint ||
            !string.Equals(target.Type, entry.ItemType, StringComparison.Ordinal) ||
            (entry.ItemType == TransferFileTypes.File && target.Size != entry.SizeBytes))
            return false;
        entry.Status = RemoteTrashStatuses.Restored;
        entry.RestoredAt ??= UtcNow();
        entry.UpdatedAt = UtcNow();
        entry.ErrorCode = null;
        await _db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    private async Task<RemoteTrashEntry> FindVisibleEntryAsync(
        int userId,
        bool isAdmin,
        Guid id,
        CancellationToken ct)
    {
        var entry = await _db.RemoteTrashEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null || (!isAdmin && entry.DeletedByUserId != userId))
            throw Error(StatusCodes.Status404NotFound, "trash_entry_not_found", "ごみ箱項目が見つかりません。");
        return entry;
    }

    private async Task<ResolvedExecution> ResolveExecutionAsync(int hostId, int shareId, CancellationToken ct)
    {
        var row = await (from h in _db.CifsHosts.AsNoTracking()
                         join s in _db.CifsShares.AsNoTracking() on h.Id equals s.HostId
                         join n in _db.ExecutionNodes.AsNoTracking().Include(x => x.GatewayNode)
                             on h.ExecutionNodeId equals n.Id
                         where h.Id == hostId && s.Id == shareId
                         select new { Host = h, Share = s, Node = n }).FirstOrDefaultAsync(ct);
        if (row is null)
            throw Error(StatusCodes.Status400BadRequest, "location_not_found", "ホスト/共有が見つかりません。");
        var password = _encryption.Decrypt(row.Host.CredPasswordEnc);
        return new ResolvedExecution(
            new CifsConnectionInfo(row.Host.HostAddress, row.Host.Port, row.Host.CredUsername,
                password, row.Share.ShareName),
            row.Node);
    }

    private async Task<TrashSettings> GetSettingsAsync(CancellationToken ct)
    {
        var values = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.TrashRetentionDays || s.Key == SettingKeys.TrashCapacityBytes)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var retention = values.TryGetValue(SettingKeys.TrashRetentionDays, out var retentionText) &&
                        int.TryParse(retentionText, out var parsedRetention)
            ? Math.Clamp(parsedRetention, 1, 3650)
            : DefaultRetentionDays;
        var capacity = values.TryGetValue(SettingKeys.TrashCapacityBytes, out var capacityText) &&
                       long.TryParse(capacityText, out var parsedCapacity) && parsedCapacity >= 0
            ? parsedCapacity
            : DefaultCapacityBytes;
        return new TrashSettings(retention, capacity);
    }

    private async Task<TransferFileMetadata?> TryGetMetadataAsync(
        ResolvedExecution execution,
        string path)
    {
        try
        {
            return await _router.GetTransferMetadataAsync(
                execution.Node, execution.Info, path, CancellationToken.None);
        }
        catch { return null; }
    }

    private async Task TrySaveAsync()
    {
        try { await _db.SaveChangesAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "リモートごみ箱の復旧状態を保存できませんでした"); }
    }

    private static RemoteTrashActionResult Result(
        RemoteTrashEntry entry,
        int nodeId,
        int? permissionId,
        string source,
        string? target,
        bool changed)
        => new(ToDto(entry), nodeId, permissionId, source, target, changed);

    private static RemoteTrashEntryDto ToDto(RemoteTrashEntry entry) => new()
    {
        Id = entry.Id,
        HostId = entry.HostId,
        ShareId = entry.ShareId,
        OriginalPath = entry.OriginalPath,
        Name = RemoteTrashPathPolicy.GetDisplayName(entry.OriginalPath),
        ItemType = entry.ItemType,
        SizeBytes = entry.SizeBytes,
        OriginalModifiedAtUtc = entry.OriginalModifiedAtUtc,
        DeletedByUsername = entry.DeletedByUsername,
        DeletedAt = entry.DeletedAt,
        ExpiresAt = entry.ExpiresAt,
        Status = entry.Status,
        RestoredPath = entry.RestoredPath,
    };

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    private static string NormalizeUserPath(string path)
    {
        try { return RemoteTrashPathPolicy.NormalizeUserPath(path); }
        catch (ArgumentException ex) { throw Error(StatusCodes.Status400BadRequest, "invalid_path", ex.Message); }
    }

    private static RemoteTrashException Error(int status, string code, string message)
        => new(status, code, message);

    private sealed record TrashSettings(int RetentionDays, long CapacityBytes);
    private sealed record ResolvedExecution(CifsConnectionInfo Info, ExecutionNode Node);
}
