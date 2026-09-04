using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

public sealed class TransferSessionException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }
    public long? ExpectedOffset { get; }
    public long? ActualOffset { get; }

    public TransferSessionException(
        int statusCode,
        string code,
        string message,
        long? expectedOffset = null,
        long? actualOffset = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        ExpectedOffset = expectedOffset;
        ActualOffset = actualOffset;
    }
}

public sealed record UploadSessionActionResult(
    UploadSessionDto Session,
    int ExecutionNodeId,
    int? PermissionId,
    bool Changed);

/// <summary>
/// Transfer v2 upload の永続状態機械。SMB 書込みと SQLite 更新は単一 transaction に
/// できないため、各操作の冒頭で実ファイル長を正とする再調整を行う。commit は先に
/// committing を永続化し、rename 後に応答が失われても target の size/hash から完了復旧する。
/// </summary>
public sealed class UploadSessionService
{
    private const long DefaultMaxFileBytes = 10L * 1024 * 1024 * 1024 * 1024;
    private static readonly KeyedAsyncLock<Guid> SessionLocks = new();
    private static readonly KeyedAsyncLock<string> IdempotencyLocks = new();
    private static readonly KeyedAsyncLock<string> TargetLocks = new();

    private readonly AppDbContext _db;
    private readonly PermissionService _permissions;
    private readonly NodeRouter _router;
    private readonly EncryptionService _encryption;
    private readonly ILogger<UploadSessionService> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _sessionLifetime;
    private readonly TimeSpan _cleanupGiveUpAfter;
    private readonly long _maxFileBytes;

    public UploadSessionService(
        AppDbContext db,
        PermissionService permissions,
        NodeRouter router,
        EncryptionService encryption,
        IConfiguration configuration,
        ILogger<UploadSessionService> logger,
        TimeProvider clock)
    {
        _db = db;
        _permissions = permissions;
        _router = router;
        _encryption = encryption;
        _logger = logger;
        _clock = clock;
        _sessionLifetime = TimeSpan.FromHours(Math.Clamp(
            configuration.GetValue<int?>("TransferV2:SessionLifetimeHours") ?? 24, 1, 24 * 30));
        _cleanupGiveUpAfter = TimeSpan.FromDays(Math.Clamp(
            configuration.GetValue<int?>("TransferV2:CleanupGiveUpDays") ?? 7, 1, 365));
        _maxFileBytes = Math.Clamp(
            configuration.GetValue<long?>("TransferV2:MaxFileBytes") ?? DefaultMaxFileBytes,
            TransferV2Limits.MaxChunkBytes,
            long.MaxValue);
    }

    public async Task<UploadSessionActionResult> CreateAsync(
        int userId,
        CreateUploadSessionRequest request,
        CancellationToken ct)
    {
        if (userId <= 0) throw Error(StatusCodes.Status401Unauthorized, "unauthorized", "認証が必要です。");
        if (request.HostId <= 0 || request.ShareId <= 0)
            throw Error(StatusCodes.Status400BadRequest, "invalid_location", "hostId と shareId は正の値で指定してください。");
        if (request.TotalSize < 0 || request.TotalSize > _maxFileBytes)
            throw Error(StatusCodes.Status400BadRequest, "invalid_total_size",
                $"totalSize は 0〜{_maxFileBytes} バイトで指定してください。");

        string targetPath;
        string expectedHash;
        string idempotencyHash;
        try
        {
            targetPath = TransferV2Validation.NormalizeUploadTargetPath(request.Path);
            expectedHash = TransferV2Validation.NormalizeSha256(request.Sha256);
            idempotencyHash = TransferV2Validation.HashIdempotencyKey(request.IdempotencyKey);
        }
        catch (ArgumentException ex)
        {
            throw Error(StatusCodes.Status400BadRequest, "invalid_request", ex.Message);
        }

        var lockKey = $"{userId}:{idempotencyHash}";
        var gate = await IdempotencyLocks.AcquireAsync(lockKey, ct);
        IDisposable? shareGate = null;
        try
        {
            shareGate = await DurableShareLock.AcquireAsync(request.ShareId, ct);
            var existing = await _db.UploadSessions.FirstOrDefaultAsync(
                s => s.UserId == userId && s.IdempotencyKeyHash == idempotencyHash, ct);
            if (existing is not null)
            {
                EnsureSameCreateRequest(existing, request, targetPath, expectedHash);
                var existingExecution = await ResolveExecutionAsync(existing.HostId, existing.ShareId, ct);
                return new UploadSessionActionResult(ToDto(existing), existingExecution.Node.Id, null, false);
            }

            var (allowed, permissionId) = await _permissions.CanPerformAsync(
                userId, request.ShareId, targetPath, Operations.Write, ct);
            if (!allowed)
                throw Error(StatusCodes.Status403Forbidden, "permission_denied", "アップロード先への書き込み権限がありません。");

            var execution = await ResolveExecutionAsync(request.HostId, request.ShareId, ct);
            var target = await _router.GetTransferMetadataAsync(
                execution.Node, execution.Info, targetPath, ct);
            EnsureUsableTarget(target, targetPath);
            if (target.Exists && !request.Overwrite)
                throw Error(StatusCodes.Status409Conflict, "target_exists",
                    "アップロード先は既に存在します。上書きを明示してください。");

            var now = UtcNow();
            var entity = new UploadSession
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                HostId = request.HostId,
                ShareId = request.ShareId,
                TargetPath = targetPath,
                IdempotencyKeyHash = idempotencyHash,
                TotalSize = request.TotalSize,
                ExpectedSha256 = expectedHash,
                UploadedOffset = 0,
                Overwrite = request.Overwrite,
                TargetExisted = target.Exists,
                TargetSize = target.Size,
                TargetModifiedAtUtc = target.ModifiedAtUtc?.ToUniversalTime(),
                Status = UploadSessionStatuses.Active,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now + _sessionLifetime,
            };
            entity.TempPath = TransferV2Validation.BuildTempPath(targetPath, entity.Id);

            // 先に台帳を永続化する。SMB上にtempだけを作ってからprocessが停止すると
            // 対応するsessionがなくjanitorから発見できない孤立ファイルになるため、
            // crash時は「台帳あり・tempなし」に寄せ、通常のreconcileで再作成可能にする。
            _db.UploadSessions.Add(entity);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                _db.Entry(entity).State = EntityState.Detached;
                var raced = await _db.UploadSessions.FirstOrDefaultAsync(
                    s => s.UserId == userId && s.IdempotencyKeyHash == idempotencyHash,
                    CancellationToken.None);
                if (raced is null) throw;
                EnsureSameCreateRequest(raced, request, targetPath, expectedHash);
                return new UploadSessionActionResult(ToDto(raced), execution.Node.Id, null, false);
            }
            catch
            {
                _db.Entry(entity).State = EntityState.Detached;
                throw;
            }

            try
            {
                var temp = await _router.EnsureTempFileAsync(
                    execution.Node, execution.Info, entity.TempPath, ct);
                if (!temp.Exists || temp.Type != TransferFileTypes.File || temp.IsReparsePoint || temp.Size != 0)
                {
                    await TryDeleteTempAsync(execution, entity.TempPath, CancellationToken.None);
                    entity.Status = UploadSessionStatuses.Failed;
                    entity.ErrorCode = "temp_path_collision";
                    entity.UpdatedAt = UtcNow();
                    await _db.SaveChangesAsync(CancellationToken.None);
                    throw Error(StatusCodes.Status409Conflict, "temp_path_collision",
                        "session用の一時パスを安全に確保できませんでした。");
                }
            }
            catch (TransferSessionException)
            {
                throw;
            }
            catch
            {
                // temp作成が到達不能等で失敗してもactive台帳を残す。次回status/chunk
                // 操作またはjanitorが同じ決定的temp pathを再調整できる。
                throw;
            }

            return new UploadSessionActionResult(ToDto(entity), execution.Node.Id, permissionId, true);
        }
        finally
        {
            shareGate?.Dispose();
            gate.Dispose();
        }
    }

    public async Task<UploadSessionActionResult> GetStatusAsync(
        int userId,
        Guid sessionId,
        CancellationToken ct)
    {
        var gate = await SessionLocks.AcquireAsync(sessionId, ct);
        try
        {
            var session = await FindOwnedAsync(userId, sessionId, ct);
            var execution = await ResolveExecutionAsync(session.HostId, session.ShareId, ct);
            await ReconcileOrExpireAsync(session, execution, ct);
            return new UploadSessionActionResult(ToDto(session), execution.Node.Id, null, false);
        }
        finally
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// 従来の単一request uploadをTransfer v2の永続台帳へ載せる。
    /// session idはclientへ公開しないが、process停止時にもjanitorがtempを特定・回収できる。
    /// </summary>
    public async Task<UploadSessionActionResult> UploadLegacyAsync(
        int userId,
        int hostId,
        int shareId,
        string path,
        long totalSize,
        Stream input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var prepared = await CreateAsync(userId, new CreateUploadSessionRequest
        {
            HostId = hostId,
            ShareId = shareId,
            Path = path,
            TotalSize = totalSize,
            // request bodyを書き終えた後にSMB実体から計算したhashへ置き換える。
            Sha256 = new string('0', 64),
            IdempotencyKey = $"legacy-{Guid.NewGuid():N}",
            Overwrite = true,
        }, ct);

        var gate = await SessionLocks.AcquireAsync(prepared.Session.SessionId, ct);
        try
        {
            var session = await FindOwnedAsync(userId, prepared.Session.SessionId, ct);
            var execution = await ResolveExecutionAsync(session.HostId, session.ShareId, ct);
            await _router.WriteTempStreamAsync(
                execution.Node, execution.Info, session.TempPath, input, ct);
            var hash = await _router.ComputeSha256Async(
                execution.Node, execution.Info, session.TempPath, ct);
            if (hash.Size != totalSize)
            {
                session.Status = UploadSessionStatuses.Failed;
                session.ErrorCode = "legacy_length_mismatch";
                session.UploadedOffset = Math.Clamp(hash.Size, 0, totalSize);
                session.UpdatedAt = UtcNow();
                await _db.SaveChangesAsync(CancellationToken.None);
                throw Error(StatusCodes.Status400BadRequest, "legacy_length_mismatch",
                    "Content-Length と実際のアップロード長が一致しません。", totalSize, hash.Size);
            }

            session.ExpectedSha256 = TransferV2Validation.NormalizeSha256(hash.Hash);
            session.UploadedOffset = hash.Size;
            session.ErrorCode = null;
            Touch(session);
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Dispose();
        }

        return await CompleteAsync(userId, prepared.Session.SessionId, ct);
    }

    public async Task<UploadSessionActionResult> WriteChunkAsync(
        int userId,
        Guid sessionId,
        long offset,
        ReadOnlyMemory<byte> chunk,
        string chunkSha256,
        CancellationToken ct)
    {
        string normalizedChunkHash;
        try
        {
            TransferV2Validation.ValidateChunk(offset, chunk.Length);
            normalizedChunkHash = TransferV2Validation.NormalizeSha256(chunkSha256, nameof(chunkSha256));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw Error(StatusCodes.Status400BadRequest, "invalid_chunk", ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw Error(StatusCodes.Status400BadRequest, "invalid_chunk", ex.Message);
        }

        var actualChunkHash = SHA256.HashData(chunk.Span);
        var expectedChunkHash = Convert.FromHexString(normalizedChunkHash);
        if (!CryptographicOperations.FixedTimeEquals(actualChunkHash, expectedChunkHash))
            throw Error(StatusCodes.Status422UnprocessableEntity, "chunk_checksum_mismatch",
                "chunk の SHA-256 が一致しません。");

        var gate = await SessionLocks.AcquireAsync(sessionId, ct);
        try
        {
            var session = await FindOwnedAsync(userId, sessionId, ct);
            if (session.Status == UploadSessionStatuses.Completed)
                return new UploadSessionActionResult(ToDto(session), 0, null, false);
            EnsureWritable(session);
            var execution = await ResolveExecutionAsync(session.HostId, session.ShareId, ct);
            await ReconcileOrExpireAsync(session, execution, ct);
            EnsureWritable(session);

            if (offset > session.TotalSize || chunk.Length > session.TotalSize - offset)
                throw Error(StatusCodes.Status416RangeNotSatisfiable, "chunk_exceeds_total_size",
                    "chunk が session の totalSize を超えます。", session.TotalSize, offset + chunk.Length);
            if (offset > session.UploadedOffset)
                throw Error(StatusCodes.Status409Conflict, "offset_mismatch",
                    "未書き込みの空白を作る offset は指定できません。", session.UploadedOffset, offset);

            TransferChunkWriteResult result;
            try
            {
                result = await _router.WriteTempChunkAsync(
                    execution.Node, execution.Info, session.TempPath, offset, chunk, ct);
            }
            catch (TransferOffsetMismatchException ex)
            {
                throw Error(StatusCodes.Status409Conflict, "offset_mismatch", ex.Message,
                    ex.ActualOffset, ex.ExpectedOffset);
            }
            if (result.NextOffset > session.TotalSize)
                throw Error(StatusCodes.Status409Conflict, "temp_size_exceeds_total",
                    "一時ファイルが session の totalSize を超えました。", session.TotalSize, result.NextOffset);

            session.UploadedOffset = result.NextOffset;
            session.ErrorCode = null;
            Touch(session);
            await _db.SaveChangesAsync(ct);
            return new UploadSessionActionResult(ToDto(session), execution.Node.Id, null, !result.AlreadyApplied);
        }
        finally
        {
            gate.Dispose();
        }
    }

    public async Task<UploadSessionActionResult> CompleteAsync(
        int userId,
        Guid sessionId,
        CancellationToken ct)
    {
        var gate = await SessionLocks.AcquireAsync(sessionId, ct);
        try
        {
            var session = await FindOwnedAsync(userId, sessionId, ct);
            var execution = await ResolveExecutionAsync(session.HostId, session.ShareId, ct);
            if (session.Status == UploadSessionStatuses.Completed)
                return new UploadSessionActionResult(ToDto(session), execution.Node.Id, null, false);
            if (session.Status is UploadSessionStatuses.Cancelled or UploadSessionStatuses.Expired or UploadSessionStatuses.Failed)
                throw TerminalStateError(session);

            var recovered = await ReconcileOrExpireAsync(session, execution, ct);
            if (recovered || session.Status == UploadSessionStatuses.Completed)
                return new UploadSessionActionResult(ToDto(session), execution.Node.Id, null, recovered);
            EnsureCompletable(session);

            // session単位のlockだけでは、別sessionが同じtargetを同時にbaseline確認して
            // 両方commitできる。物理target単位で確認からrename・完了保存まで直列化する。
            using var targetGate = await TargetLocks.AcquireAsync(BuildTargetLockKey(session), ct);

            // create 時の許可が途中で失効・削除されてもcommitできないよう、rename直前に再評価する。
            var (allowed, permissionId) = await _permissions.CanPerformAsync(
                userId, session.ShareId, session.TargetPath, Operations.Write, ct);
            if (!allowed)
                throw Error(StatusCodes.Status403Forbidden, "permission_revoked",
                    "アップロード先への書き込み権限が失効しています。");

            if (session.UploadedOffset != session.TotalSize)
                throw Error(StatusCodes.Status409Conflict, "upload_incomplete",
                    "アップロードが完了していません。", session.TotalSize, session.UploadedOffset);

            var hash = await _router.ComputeSha256Async(
                execution.Node, execution.Info, session.TempPath, ct);
            if (hash.Size != session.TotalSize)
            {
                session.UploadedOffset = Math.Clamp(hash.Size, 0, session.TotalSize);
                Touch(session);
                await _db.SaveChangesAsync(ct);
                throw Error(StatusCodes.Status409Conflict, "temp_size_mismatch",
                    "一時ファイルのサイズが totalSize と一致しません。", session.TotalSize, hash.Size);
            }
            if (!FixedTimeHashEquals(hash.Hash, session.ExpectedSha256))
            {
                session.Status = UploadSessionStatuses.Failed;
                session.ErrorCode = "total_checksum_mismatch";
                session.UpdatedAt = UtcNow();
                await _db.SaveChangesAsync(ct);
                await TryDeleteTempAsync(execution, session.TempPath, CancellationToken.None);
                throw Error(StatusCodes.Status422UnprocessableEntity, "total_checksum_mismatch",
                    "ファイル全体の SHA-256 が一致しません。");
            }

            var currentTarget = await _router.GetTransferMetadataAsync(
                execution.Node, execution.Info, session.TargetPath, ct);
            EnsureUsableTarget(currentTarget, session.TargetPath);
            if (!TargetMatchesBaseline(session, currentTarget))
            {
                session.ErrorCode = "target_conflict";
                session.UpdatedAt = UtcNow();
                await _db.SaveChangesAsync(ct);
                throw Error(StatusCodes.Status409Conflict, "target_conflict",
                    "アップロード開始後に対象ファイルが変更されました。commitを中止しました。");
            }

            session.Status = UploadSessionStatuses.Committing;
            session.ErrorCode = null;
            session.UpdatedAt = UtcNow();
            await _db.SaveChangesAsync(ct);

            // target がcreate時に存在した場合だけ置換を許す。開始時に存在しなかったtargetへ
            // replace=falseを渡すことで、metadata確認直後に作られたファイルも上書きしない。
            var replaceIfExists = session.TargetExisted && session.Overwrite;
            await _router.CommitTempAsync(
                execution.Node,
                execution.Info,
                session.TempPath,
                session.TargetPath,
                replaceIfExists,
                ct);

            var committed = await _router.GetTransferMetadataAsync(
                execution.Node, execution.Info, session.TargetPath, ct);
            EnsureCommittedMetadata(session, committed);
            MarkCompleted(session, committed);
            await _db.SaveChangesAsync(ct);
            return new UploadSessionActionResult(ToDto(session), execution.Node.Id, permissionId, true);
        }
        finally
        {
            gate.Dispose();
        }
    }

    public async Task<UploadSessionActionResult> CancelAsync(
        int userId,
        Guid sessionId,
        CancellationToken ct)
    {
        var gate = await SessionLocks.AcquireAsync(sessionId, ct);
        try
        {
            var session = await FindOwnedAsync(userId, sessionId, ct);
            var execution = await ResolveExecutionAsync(session.HostId, session.ShareId, ct);
            if (session.Status == UploadSessionStatuses.Completed)
                return new UploadSessionActionResult(ToDto(session), execution.Node.Id, null, false);
            if (session.Status is UploadSessionStatuses.Cancelled or UploadSessionStatuses.Expired)
                return new UploadSessionActionResult(ToDto(session), execution.Node.Id, null, false);

            if (session.Status == UploadSessionStatuses.Committing &&
                await TryRecoverCommittedAsync(session, execution, ct))
            {
                return new UploadSessionActionResult(ToDto(session), execution.Node.Id, null, true);
            }

            await _router.DeleteTempAsync(execution.Node, execution.Info, session.TempPath, ct);
            session.Status = UploadSessionStatuses.Cancelled;
            session.ErrorCode = null;
            session.UpdatedAt = UtcNow();
            await _db.SaveChangesAsync(ct);
            return new UploadSessionActionResult(ToDto(session), execution.Node.Id, null, true);
        }
        finally
        {
            gate.Dispose();
        }
    }

    /// <summary>期限切れ session と対応する一時ファイルをbest effortで回収する。</summary>
    public async Task<int> ExpireSessionsAsync(CancellationToken ct)
    {
        var now = UtcNow();
        var ids = await _db.UploadSessions.AsNoTracking()
            .Where(s => (s.Status == UploadSessionStatuses.Active ||
                         s.Status == UploadSessionStatuses.Committing ||
                         s.Status == UploadSessionStatuses.Failed) &&
                        s.ExpiresAt <= now)
            .OrderBy(s => s.ExpiresAt)
            .Select(s => s.Id)
            .Take(100)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var gate = await SessionLocks.AcquireAsync(id, ct);
            try
            {
                var session = await _db.UploadSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
                if (session is null || session.ExpiresAt > UtcNow() ||
                    session.Status is UploadSessionStatuses.Completed or UploadSessionStatuses.Cancelled or UploadSessionStatuses.Expired)
                    continue;
                var execution = await ResolveExecutionAsync(session.HostId, session.ShareId, ct);
                if (session.Status == UploadSessionStatuses.Committing &&
                    await TryRecoverCommittedAsync(session, execution, ct))
                    continue;

                await _router.DeleteTempAsync(execution.Node, execution.Info, session.TempPath, ct);
                session.Status = UploadSessionStatuses.Expired;
                session.ErrorCode = null;
                session.UpdatedAt = UtcNow();
                await _db.SaveChangesAsync(ct);
                expired++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transfer v2 session {SessionId} の期限切れcleanupに失敗しました", id);
                _db.ChangeTracker.Clear();
                await DeferFailedCleanupAsync(id, ct);
            }
            finally
            {
                gate.Dispose();
            }
        }
        return expired;
    }

    /// <summary>
    /// 共有の廃止・付け替えのために、その共有に残る未完了 session を管理者権限で終端させる。
    /// まず正規の後片付け (一時ファイルの削除) を試し、到達できないものだけ台帳を諦める。
    /// 諦めた分は共有上にゴミとして残るため、パスを呼び出し側へ返して監査ログに残させる。
    /// </summary>
    public async Task<(int CleanedUp, int Abandoned, List<string> OrphanedPaths)> ReleaseForShareAsync(
        int shareId,
        CancellationToken ct)
    {
        var ids = await _db.UploadSessions.AsNoTracking()
            .Where(s => s.ShareId == shareId &&
                        (s.Status == UploadSessionStatuses.Active ||
                         s.Status == UploadSessionStatuses.Committing ||
                         s.Status == UploadSessionStatuses.Failed))
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var cleanedUp = 0;
        var abandoned = 0;
        var orphaned = new List<string>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var gate = await SessionLocks.AcquireAsync(id, ct);
            try
            {
                var session = await _db.UploadSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
                if (session is null || session.Status is UploadSessionStatuses.Completed or
                    UploadSessionStatuses.Cancelled or UploadSessionStatuses.Expired)
                    continue;

                try
                {
                    var execution = await ResolveExecutionAsync(session.HostId, session.ShareId, ct);
                    await _router.DeleteTempAsync(execution.Node, execution.Info, session.TempPath, ct);
                    session.ErrorCode = null;
                    cleanedUp++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 共有が既に到達不能なケースがこの機能の本題。実体を消せないことは
                    // 失敗にせず、台帳を終端させたうえで残骸のパスを呼び出し側へ返す。
                    _logger.LogWarning(ex,
                        "共有 {ShareId} の session {SessionId} は実体を回収できないまま終端させます", shareId, id);
                    session.ErrorCode = TransferCleanupErrorCodes.AdminAbandoned;
                    orphaned.Add(session.TempPath);
                    abandoned++;
                }

                session.Status = UploadSessionStatuses.Cancelled;
                session.UpdatedAt = UtcNow();
                await _db.SaveChangesAsync(ct);
            }
            finally
            {
                gate.Dispose();
            }
        }
        return (cleanedUp, abandoned, orphaned);
    }

    private async Task<bool> ReconcileOrExpireAsync(
        UploadSession session,
        ResolvedExecution execution,
        CancellationToken ct)
    {
        if (session.Status is UploadSessionStatuses.Completed or UploadSessionStatuses.Cancelled or
            UploadSessionStatuses.Expired or UploadSessionStatuses.Failed)
            return false;
        if (session.ExpiresAt <= UtcNow())
        {
            await _router.DeleteTempAsync(execution.Node, execution.Info, session.TempPath, ct);
            session.Status = UploadSessionStatuses.Expired;
            session.ErrorCode = null;
            session.UpdatedAt = UtcNow();
            await _db.SaveChangesAsync(ct);
            return false;
        }

        var metadata = await _router.GetTransferMetadataAsync(
            execution.Node, execution.Info, session.TempPath, ct);
        if (!metadata.Exists)
        {
            if (session.Status == UploadSessionStatuses.Committing &&
                await TryRecoverCommittedAsync(session, execution, ct))
                return true;

            if (session.Status == UploadSessionStatuses.Active)
            {
                // SMB書込み後のtemp削除など、DBより実ファイルを正として再開する。
                // totalSize=0 はchunkを送れないため、status/complete経路で空tempも再作成する。
                var recreated = await _router.EnsureTempFileAsync(
                    execution.Node, execution.Info, session.TempPath, ct);
                EnsureRegularTemp(recreated, session.TempPath);
                var recreatedSize = recreated.Size ?? 0;
                if (recreatedSize > session.TotalSize)
                {
                    session.Status = UploadSessionStatuses.Failed;
                    session.ErrorCode = "temp_size_exceeds_total";
                    session.UpdatedAt = UtcNow();
                    await _db.SaveChangesAsync(ct);
                    throw Error(StatusCodes.Status409Conflict, "temp_size_exceeds_total",
                        "再作成された一時ファイルが session の totalSize を超えています。",
                        session.TotalSize, recreatedSize);
                }
                session.UploadedOffset = recreatedSize;
                session.ErrorCode = "temp_missing_restarted";
                session.UpdatedAt = UtcNow();
                await _db.SaveChangesAsync(ct);
            }
            return false;
        }

        EnsureRegularTemp(metadata, session.TempPath);
        var actualSize = metadata.Size ?? 0;
        if (actualSize > session.TotalSize)
        {
            session.Status = UploadSessionStatuses.Failed;
            session.ErrorCode = "temp_size_exceeds_total";
            session.UpdatedAt = UtcNow();
            await _db.SaveChangesAsync(ct);
            throw Error(StatusCodes.Status409Conflict, "temp_size_exceeds_total",
                "一時ファイルが session の totalSize を超えています。", session.TotalSize, actualSize);
        }
        if (actualSize != session.UploadedOffset)
        {
            session.UploadedOffset = actualSize;
            session.UpdatedAt = UtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return false;
    }

    private async Task<bool> TryRecoverCommittedAsync(
        UploadSession session,
        ResolvedExecution execution,
        CancellationToken ct)
    {
        if (session.Status != UploadSessionStatuses.Committing) return false;
        var temp = await _router.GetTransferMetadataAsync(
            execution.Node, execution.Info, session.TempPath, ct);
        if (temp.Exists) return false;

        var target = await _router.GetTransferMetadataAsync(
            execution.Node, execution.Info, session.TargetPath, ct);
        if (!target.Exists || target.IsReparsePoint || target.Type != TransferFileTypes.File ||
            target.Size != session.TotalSize)
            return false;

        var hash = await _router.ComputeSha256Async(
            execution.Node, execution.Info, session.TargetPath, ct);
        if (hash.Size != session.TotalSize || !FixedTimeHashEquals(hash.Hash, session.ExpectedSha256))
            return false;

        MarkCompleted(session, target);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<UploadSession> FindOwnedAsync(int userId, Guid sessionId, CancellationToken ct)
    {
        var session = await _db.UploadSessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && s.UserId == userId, ct);
        return session ?? throw Error(StatusCodes.Status404NotFound, "session_not_found",
            "アップロード session が見つかりません。");
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
        var info = new CifsConnectionInfo(
            row.Host.HostAddress,
            row.Host.Port,
            row.Host.CredUsername,
            password,
            row.Share.ShareName);
        return new ResolvedExecution(info, row.Node);
    }

    private static void EnsureSameCreateRequest(
        UploadSession existing,
        CreateUploadSessionRequest request,
        string normalizedTarget,
        string expectedHash)
    {
        if (existing.HostId != request.HostId || existing.ShareId != request.ShareId ||
            !string.Equals(existing.TargetPath, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
            existing.TotalSize != request.TotalSize ||
            !string.Equals(existing.ExpectedSha256, expectedHash, StringComparison.Ordinal) ||
            existing.Overwrite != request.Overwrite)
            throw Error(StatusCodes.Status409Conflict, "idempotency_conflict",
                "同じ idempotencyKey が異なるアップロード要求に使われています。");
    }

    private static void EnsureUsableTarget(TransferFileMetadata metadata, string path)
    {
        if (!metadata.Exists) return;
        if (metadata.IsReparsePoint)
            throw Error(StatusCodes.Status409Conflict, "reparse_point_rejected",
                $"リパースポイントは対象にできません: {path}");
        if (metadata.Type != TransferFileTypes.File)
            throw Error(StatusCodes.Status409Conflict, "target_not_file",
                $"アップロード先は通常ファイルである必要があります: {path}");
    }

    private static void EnsureRegularTemp(TransferFileMetadata metadata, string path)
    {
        if (!metadata.Exists || metadata.IsReparsePoint || metadata.Type != TransferFileTypes.File)
            throw Error(StatusCodes.Status409Conflict, "invalid_temp_file",
                $"session の一時ファイルが通常ファイルではありません: {path}");
    }

    private static bool TargetMatchesBaseline(UploadSession session, TransferFileMetadata current)
    {
        if (!session.TargetExisted) return !current.Exists;
        return current.Exists && !current.IsReparsePoint && current.Type == TransferFileTypes.File &&
               MetadataEqualsBaseline(session, current);
    }

    private static bool MetadataEqualsBaseline(UploadSession session, TransferFileMetadata current)
        => current.Size == session.TargetSize &&
           UtcTicks(current.ModifiedAtUtc) == UtcTicks(session.TargetModifiedAtUtc);

    private static long? UtcTicks(DateTime? value) => value?.ToUniversalTime().Ticks;

    private static void EnsureCommittedMetadata(UploadSession session, TransferFileMetadata metadata)
    {
        EnsureUsableTarget(metadata, session.TargetPath);
        if (!metadata.Exists || metadata.Size != session.TotalSize)
            throw Error(StatusCodes.Status409Conflict, "commit_metadata_mismatch",
                "commit後のファイルサイズが totalSize と一致しません。",
                session.TotalSize, metadata.Size);
    }

    private void MarkCompleted(UploadSession session, TransferFileMetadata metadata)
    {
        var now = UtcNow();
        session.Status = UploadSessionStatuses.Completed;
        session.UploadedOffset = session.TotalSize;
        session.ErrorCode = null;
        session.CommittedETag = TransferV2Validation.BuildMetadataETag(metadata);
        session.CompletedAt = now;
        session.UpdatedAt = now;
    }

    private void Touch(UploadSession session)
    {
        var now = UtcNow();
        session.UpdatedAt = now;
        session.ExpiresAt = now + _sessionLifetime;
    }

    private static void EnsureWritable(UploadSession session)
    {
        if (session.Status != UploadSessionStatuses.Active)
            throw TerminalStateError(session);
    }

    private static void EnsureCompletable(UploadSession session)
    {
        if (session.Status is not (UploadSessionStatuses.Active or UploadSessionStatuses.Committing))
            throw TerminalStateError(session);
    }

    private static TransferSessionException TerminalStateError(UploadSession session)
    {
        var status = session.Status == UploadSessionStatuses.Expired
            ? StatusCodes.Status410Gone
            : StatusCodes.Status409Conflict;
        return Error(status, $"session_{session.Status}",
            $"session は {session.Status} 状態のため、この操作を実行できません。");
    }

    private static bool FixedTimeHashEquals(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(TransferV2Validation.NormalizeSha256(left));
            var rightBytes = Convert.FromHexString(TransferV2Validation.NormalizeSha256(right));
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task TryDeleteTempAsync(
        ResolvedExecution execution,
        string tempPath,
        CancellationToken ct)
    {
        try
        {
            await _router.DeleteTempAsync(execution.Node, execution.Info, tempPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transfer v2 一時ファイル {TempPath} の削除に失敗しました", tempPath);
        }
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    private static string BuildTargetLockKey(UploadSession session)
        => $"{session.HostId}:{session.ShareId}:{session.TargetPath.ToUpperInvariant()}";

    private async Task DeferFailedCleanupAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            var session = await _db.UploadSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            if (session is null) return;

            // 共有そのものが到達不能になると実体の削除は二度と成功しない。無期限に再試行すると
            // 掃引のたびに死んだホストへ接続を試み、警告ログを出し続け、共有の削除・付け替えも
            // 永久にブロックされる。作成から十分に経った session は諦めて終端させる。
            // committing だけは次回に完了復旧できる可能性があるので対象外。
            if (session.Status != UploadSessionStatuses.Committing &&
                UtcNow() > session.CreatedAt + _sessionLifetime + _cleanupGiveUpAfter)
            {
                _logger.LogWarning(
                    "Transfer v2 session {SessionId} の実体を回収できないまま諦めます。" +
                    "共有上に {TempPath} が残っている可能性があります",
                    sessionId, session.TempPath);
                session.Status = UploadSessionStatuses.Cancelled;
                session.ErrorCode = TransferCleanupErrorCodes.CleanupGaveUp;
                session.UpdatedAt = UtcNow();
                await _db.SaveChangesAsync(ct);
                return;
            }

            // 失敗した先頭100件が毎回選ばれて後続を永久に飢餓させないよう、
            // retry対象を一時的に将来へ送る。active/failedは既に期限切れなのでterminalへ、
            // committingだけは次回に完了復旧できる状態を維持する。
            if (session.Status != UploadSessionStatuses.Committing)
                session.Status = UploadSessionStatuses.Failed;
            session.ErrorCode = TransferCleanupErrorCodes.CleanupRetry;
            session.UpdatedAt = UtcNow();
            session.ExpiresAt = UtcNow().AddMinutes(5);
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception retryEx)
        {
            _logger.LogWarning(retryEx,
                "Transfer v2 session {SessionId} のcleanup再試行延期を保存できませんでした", sessionId);
            _db.ChangeTracker.Clear();
        }
    }

    public static UploadSessionDto ToDto(UploadSession session) => new()
    {
        SessionId = session.Id,
        Status = session.Status,
        HostId = session.HostId,
        ShareId = session.ShareId,
        Path = session.TargetPath,
        TotalSize = session.TotalSize,
        UploadedOffset = session.UploadedOffset,
        Sha256 = session.ExpectedSha256,
        Overwrite = session.Overwrite,
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt,
        ExpiresAt = session.ExpiresAt,
        CompletedAt = session.CompletedAt,
        ErrorCode = session.ErrorCode,
        ETag = session.CommittedETag,
    };

    private static TransferSessionException Error(
        int status,
        string code,
        string message,
        long? expectedOffset = null,
        long? actualOffset = null)
        => new(status, code, message, expectedOffset, actualOffset);

    private sealed record ResolvedExecution(CifsConnectionInfo Info, ExecutionNode Node);
}
