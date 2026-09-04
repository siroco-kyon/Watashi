using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Watashi.Server.Data;
using Watashi.Server.Endpoints;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public sealed class UploadSessionServiceTests
{
    [Fact]
    public async Task Create_is_idempotent_and_temp_is_in_target_parent()
    {
        using var f = await Fixture.CreateAsync();
        var bytes = new byte[] { 1, 2, 3 };
        var request = f.Request("/folder/a.bin", bytes, "same-key");

        var first = await f.Service.CreateAsync(f.UserId, request, CancellationToken.None);
        var replay = await f.Service.CreateAsync(f.UserId, request, CancellationToken.None);

        first.Changed.Should().BeTrue();
        replay.Changed.Should().BeFalse();
        replay.Session.SessionId.Should().Be(first.Session.SessionId);
        f.Router.Files.Keys.Should().ContainSingle(path =>
            PathHelper.GetParent(path) == "/folder" && path.Contains(".watashi-upload-"));
        (await f.Db.UploadSessions.SingleAsync()).IdempotencyKeyHash
            .Should().NotContain("same-key");

        var conflict = async () => await f.Service.CreateAsync(
            f.UserId, request with { Path = "/folder/other.bin" }, CancellationToken.None);
        await conflict.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.Code == "idempotency_conflict");
    }

    [Fact]
    public async Task Create_persists_reconcilable_session_before_temp_creation()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.EnsureTempFailure = new IOException("SMB unavailable");

        var act = async () => await f.Service.CreateAsync(
            f.UserId, f.Request("/recover-later.bin", new byte[] { 1 }),
            CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        var persisted = await f.Db.UploadSessions.SingleAsync();
        persisted.Status.Should().Be(UploadSessionStatuses.Active);
        persisted.TempPath.Should().Contain(TransferV2Limits.TempFilePrefix);
        f.Router.Files.Should().NotContainKey(persisted.TempPath);
    }

    [Fact]
    public async Task Chunk_checksum_replay_and_db_offset_reconciliation_are_safe()
    {
        using var f = await Fixture.CreateAsync();
        var bytes = new byte[] { 4, 5, 6, 7 };
        var created = await f.Service.CreateAsync(
            f.UserId, f.Request("/a.bin", bytes), CancellationToken.None);

        var first = await f.Service.WriteChunkAsync(
            f.UserId, created.Session.SessionId, 0, bytes, Hash(bytes), CancellationToken.None);
        var replay = await f.Service.WriteChunkAsync(
            f.UserId, created.Session.SessionId, 0, bytes, Hash(bytes), CancellationToken.None);
        first.Session.UploadedOffset.Should().Be(bytes.Length);
        replay.Session.UploadedOffset.Should().Be(bytes.Length);
        replay.Changed.Should().BeFalse();

        var row = await f.Db.UploadSessions.SingleAsync();
        row.UploadedOffset = 0; // SMB成功後・DB保存前のクラッシュを模擬する。
        await f.Db.SaveChangesAsync();
        var reconciled = await f.Service.GetStatusAsync(
            f.UserId, row.Id, CancellationToken.None);
        reconciled.Session.UploadedOffset.Should().Be(bytes.Length);

        var badChecksum = async () => await f.Service.WriteChunkAsync(
            f.UserId, row.Id, 0, bytes, new string('0', 64), CancellationToken.None);
        await badChecksum.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.Code == "chunk_checksum_mismatch");
    }

    [Fact]
    public async Task Complete_commits_atomically_once_and_retry_returns_completed()
    {
        using var f = await Fixture.CreateAsync();
        var bytes = new byte[] { 9, 8, 7, 6 };
        var created = await f.Service.CreateAsync(
            f.UserId, f.Request("/done.bin", bytes), CancellationToken.None);
        await f.Service.WriteChunkAsync(
            f.UserId, created.Session.SessionId, 0, bytes, Hash(bytes), CancellationToken.None);

        var completed = await f.Service.CompleteAsync(
            f.UserId, created.Session.SessionId, CancellationToken.None);
        var replay = await f.Service.CompleteAsync(
            f.UserId, created.Session.SessionId, CancellationToken.None);

        completed.Session.Status.Should().Be(UploadSessionStatuses.Completed);
        completed.Session.ETag.Should().StartWith("\"").And.EndWith("\"");
        replay.Changed.Should().BeFalse();
        f.Router.CommitCount.Should().Be(1);
        f.Router.Files["/done.bin"].Bytes.Should().Equal(bytes);
        f.Router.Files.Keys.Should().NotContain(path => path.Contains(".watashi-upload-"));
    }

    [Fact]
    public async Task Zero_byte_upload_recreates_missing_temp_and_completes()
    {
        using var f = await Fixture.CreateAsync();
        var bytes = Array.Empty<byte>();
        var created = await f.Service.CreateAsync(
            f.UserId, f.Request("/empty.bin", bytes), CancellationToken.None);
        var row = await f.Db.UploadSessions.SingleAsync();
        f.Router.Files.Remove(row.TempPath);

        var completed = await f.Service.CompleteAsync(
            f.UserId, created.Session.SessionId, CancellationToken.None);

        completed.Session.Status.Should().Be(UploadSessionStatuses.Completed);
        f.Router.Files["/empty.bin"].Bytes.Should().BeEmpty();
        f.Router.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task Commit_rechecks_permission_and_detects_target_metadata_conflict()
    {
        using var revoked = await Fixture.CreateAsync();
        var bytes = new byte[] { 1, 1 };
        var session = await revoked.Service.CreateAsync(
            revoked.UserId, revoked.Request("/revoked.bin", bytes), CancellationToken.None);
        await revoked.Service.WriteChunkAsync(
            revoked.UserId, session.Session.SessionId, 0, bytes, Hash(bytes), CancellationToken.None);
        revoked.Db.UserPermissions.RemoveRange(revoked.Db.UserPermissions);
        await revoked.Db.SaveChangesAsync();

        var denied = async () => await revoked.Service.CompleteAsync(
            revoked.UserId, session.Session.SessionId, CancellationToken.None);
        await denied.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.Code == "permission_revoked");
        revoked.Router.CommitCount.Should().Be(0);

        using var changed = await Fixture.CreateAsync();
        changed.Router.PutFile("/existing.bin", new byte[] { 3 }, changed.Clock.GetUtcNow().UtcDateTime);
        var overwrite = await changed.Service.CreateAsync(
            changed.UserId,
            changed.Request("/existing.bin", bytes) with { Overwrite = true },
            CancellationToken.None);
        await changed.Service.WriteChunkAsync(
            changed.UserId, overwrite.Session.SessionId, 0, bytes, Hash(bytes), CancellationToken.None);
        changed.Router.PutFile("/existing.bin", new byte[] { 3, 4 },
            changed.Clock.GetUtcNow().UtcDateTime.AddSeconds(1));

        var conflict = async () => await changed.Service.CompleteAsync(
            changed.UserId, overwrite.Session.SessionId, CancellationToken.None);
        await conflict.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.Code == "target_conflict");
        changed.Router.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Total_checksum_mismatch_fails_without_publishing_target()
    {
        using var f = await Fixture.CreateAsync();
        var expected = new byte[] { 1, 2, 3 };
        var uploaded = new byte[] { 1, 2, 4 };
        var created = await f.Service.CreateAsync(
            f.UserId, f.Request("/bad.bin", expected), CancellationToken.None);
        await f.Service.WriteChunkAsync(
            f.UserId, created.Session.SessionId, 0, uploaded, Hash(uploaded),
            CancellationToken.None);

        var act = async () => await f.Service.CompleteAsync(
            f.UserId, created.Session.SessionId, CancellationToken.None);

        await act.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.Code == "total_checksum_mismatch");
        var row = await f.Db.UploadSessions.SingleAsync();
        row.Status.Should().Be(UploadSessionStatuses.Failed);
        f.Router.CommitCount.Should().Be(0);
        f.Router.Files.Should().NotContainKey("/bad.bin");
        f.Router.Files.Should().NotContainKey(row.TempPath);
    }

    [Fact]
    public async Task Committing_session_recovers_after_rename_without_double_commit()
    {
        using var f = await Fixture.CreateAsync();
        var bytes = new byte[] { 2, 4, 6 };
        var created = await f.Service.CreateAsync(
            f.UserId, f.Request("/recovered.bin", bytes), CancellationToken.None);
        await f.Service.WriteChunkAsync(
            f.UserId, created.Session.SessionId, 0, bytes, Hash(bytes), CancellationToken.None);
        var row = await f.Db.UploadSessions.SingleAsync();
        row.Status = UploadSessionStatuses.Committing;
        await f.Db.SaveChangesAsync();
        f.Router.SimulateRenameWithoutAcknowledgement(row.TempPath, row.TargetPath);

        var recovered = await f.Service.CompleteAsync(f.UserId, row.Id, CancellationToken.None);

        recovered.Session.Status.Should().Be(UploadSessionStatuses.Completed);
        f.Router.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Expired_session_deletes_temp_and_other_user_cannot_observe_it()
    {
        using var f = await Fixture.CreateAsync();
        var bytes = new byte[] { 8 };
        var created = await f.Service.CreateAsync(
            f.UserId, f.Request("/old.bin", bytes), CancellationToken.None);

        var other = new User
        {
            Username = "other",
            PasswordHash = "x",
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedAt = DateTime.UtcNow,
        };
        f.Db.Users.Add(other);
        await f.Db.SaveChangesAsync();
        var hidden = async () => await f.Service.GetStatusAsync(
            other.Id, created.Session.SessionId, CancellationToken.None);
        await hidden.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.StatusCode == 404);

        f.Clock.Advance(TimeSpan.FromHours(25));
        (await f.Service.ExpireSessionsAsync(CancellationToken.None)).Should().Be(1);
        var row = await f.Db.UploadSessions.SingleAsync();
        row.Status.Should().Be(UploadSessionStatuses.Expired);
        f.Router.Files.Keys.Should().NotContain(path => path.Contains(".watashi-upload-"));
    }

    [Fact]
    public async Task Concurrent_sessions_for_same_target_are_serialized_before_baseline_and_commit()
    {
        using var f = await Fixture.CreateAsync();
        var firstBytes = new byte[] { 1, 2 };
        var secondBytes = new byte[] { 3, 4 };
        var first = await f.Service.CreateAsync(
            f.UserId, f.Request("/same.bin", firstBytes, "same-target-1"), CancellationToken.None);
        var second = await f.Service.CreateAsync(
            f.UserId, f.Request("/same.bin", secondBytes, "same-target-2"), CancellationToken.None);
        await f.Service.WriteChunkAsync(
            f.UserId, first.Session.SessionId, 0, firstBytes, Hash(firstBytes), CancellationToken.None);
        await f.Service.WriteChunkAsync(
            f.UserId, second.Session.SessionId, 0, secondBytes, Hash(secondBytes), CancellationToken.None);

        f.Router.PauseCommits();
        var firstComplete = f.Service.CompleteAsync(
            f.UserId, first.Session.SessionId, CancellationToken.None);
        await f.Router.WaitForCommitEntryAsync();
        f.Router.ObserveNextTempMetadata();
        var secondComplete = f.Service.CompleteAsync(
            f.UserId, second.Session.SessionId, CancellationToken.None);
        await f.Router.WaitForObservedTempMetadataAsync();
        await Task.Yield();

        f.Router.CommitEnteredCount.Should().Be(1,
            "the second session must wait on the target lock before baseline/commit");
        f.Router.ReleaseCommits();
        (await firstComplete).Session.Status.Should().Be(UploadSessionStatuses.Completed);
        var conflict = async () => await secondComplete;
        await conflict.Should().ThrowAsync<TransferSessionException>()
            .Where(x => x.Code == "target_conflict");
        f.Router.CommitCount.Should().Be(1);
        f.Router.Files["/same.bin"].Bytes.Should().Equal(firstBytes);
    }

    [Fact]
    public async Task Share_durable_lock_covers_session_row_and_temp_creation()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PauseEnsureTemp();
        var create = f.Service.CreateAsync(
            f.UserId, f.Request("/locked.bin", new byte[] { 1 }), CancellationToken.None);
        await f.Router.WaitForEnsureTempEntryAsync();

        var adminLease = DurableShareLock.AcquireAsync(f.ShareId, CancellationToken.None).AsTask();
        await Task.Yield();
        adminLease.IsCompleted.Should().BeFalse(
            "share mutation must wait until both the durable row and physical temp are established");

        f.Router.ReleaseEnsureTemp();
        await create;
        using (await adminLease)
        {
            (await Watashi.Server.Endpoints.AdminShareEndpoints.HasDurableTransferStateAsync(
                f.Db, f.ShareId, CancellationToken.None)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Share_delete_lock_prevents_session_creation_after_durable_check()
    {
        using var f = await Fixture.CreateAsync();
        var adminLease = await DurableShareLock.AcquireAsync(f.ShareId, CancellationToken.None);
        var create = f.Service.CreateAsync(
            f.UserId, f.Request("/racing.bin", new byte[] { 1 }), CancellationToken.None);
        await Task.Yield();
        (await f.Db.UploadSessions.CountAsync()).Should().Be(0);

        var share = await f.Db.CifsShares.SingleAsync(s => s.Id == f.ShareId);
        f.Db.CifsShares.Remove(share);
        await f.Db.SaveChangesAsync();
        adminLease.Dispose();

        var failed = async () => await create;
        await failed.Should().ThrowAsync<TransferSessionException>();
        (await f.Db.UploadSessions.CountAsync()).Should().Be(0);
        f.Router.Files.Keys.Should().NotContain(path => path.Contains(".watashi-upload-"));
    }

    [Fact]
    public async Task Legacy_streaming_upload_uses_durable_session_and_publishes_atomically()
    {
        using var f = await Fixture.CreateAsync();
        var bytes = new byte[] { 7, 8, 9 };

        var result = await f.Service.UploadLegacyAsync(
            f.UserId, f.HostId, f.ShareId, "/legacy.bin", bytes.Length,
            new MemoryStream(bytes), CancellationToken.None);

        result.Session.Status.Should().Be(UploadSessionStatuses.Completed);
        f.Router.Files["/legacy.bin"].Bytes.Should().Equal(bytes);
        f.Router.Files.Keys.Should().NotContain(path => path.Contains(".watashi-upload-"));
        (await f.Db.UploadSessions.SingleAsync()).Status.Should().Be(UploadSessionStatuses.Completed);
    }

    [Fact]
    public async Task Legacy_stream_failure_leaves_durable_row_for_janitor_cleanup()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.WriteTempStreamFailure = new IOException("stream interrupted");

        var act = async () => await f.Service.UploadLegacyAsync(
            f.UserId, f.HostId, f.ShareId, "/legacy-failed.bin", 3,
            new MemoryStream(new byte[] { 1, 2, 3 }), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        var row = await f.Db.UploadSessions.SingleAsync();
        row.Status.Should().Be(UploadSessionStatuses.Active);
        row.TempPath.Should().Contain(TransferV2Limits.TempFilePrefix);
    }

    [Fact]
    public async Task Cleanup_failure_is_deferred_so_later_expired_sessions_are_not_starved()
    {
        using var f = await Fixture.CreateAsync();
        await f.Service.CreateAsync(
            f.UserId, f.Request("/retry.bin", new byte[] { 8 }), CancellationToken.None);
        f.Clock.Advance(TimeSpan.FromHours(25));
        f.Router.DeleteTempFailure = new IOException("SMB unavailable");

        (await f.Service.ExpireSessionsAsync(CancellationToken.None)).Should().Be(0);

        var row = await f.Db.UploadSessions.SingleAsync();
        row.Status.Should().Be(UploadSessionStatuses.Failed);
        row.ErrorCode.Should().Be("cleanup_retry");
        row.ExpiresAt.Should().Be(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(5));
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Release_for_share_removes_the_temp_file_when_the_share_is_reachable()
    {
        using var f = await Fixture.CreateAsync();
        await f.Service.CreateAsync(f.UserId, f.Request("/a.bin", new byte[] { 1 }), CancellationToken.None);

        var result = await f.Service.ReleaseForShareAsync(f.ShareId, CancellationToken.None);

        result.CleanedUp.Should().Be(1);
        result.Abandoned.Should().Be(0);
        result.OrphanedPaths.Should().BeEmpty();
        f.Router.Files.Should().BeEmpty("実体まで回収できたため");
        var session = await f.Db.UploadSessions.SingleAsync();
        session.Status.Should().Be(UploadSessionStatuses.Cancelled);
        session.ErrorCode.Should().BeNull();
        (await AdminShareEndpoints.HasDurableTransferStateAsync(f.Db, f.ShareId, CancellationToken.None))
            .Should().BeFalse("解除後は共有の付け替え・削除がブロックされない");
    }

    [Fact]
    public async Task Release_for_share_abandons_the_ledger_when_the_share_is_unreachable()
    {
        using var f = await Fixture.CreateAsync();
        var created = await f.Service.CreateAsync(
            f.UserId, f.Request("/a.bin", new byte[] { 1 }), CancellationToken.None);
        // 共有サーバが落ちて実体を消せない状態。この機能が本来救うべきケース。
        f.Router.DeleteTempFailure = new IOException("share is gone");

        var result = await f.Service.ReleaseForShareAsync(f.ShareId, CancellationToken.None);

        result.CleanedUp.Should().Be(0);
        result.Abandoned.Should().Be(1);
        result.OrphanedPaths.Should().ContainSingle()
            .Which.Should().Contain(".watashi-upload-");
        var session = await f.Db.UploadSessions.SingleAsync();
        session.Id.Should().Be(created.Session.SessionId);
        session.Status.Should().Be(UploadSessionStatuses.Cancelled);
        session.ErrorCode.Should().Be(TransferCleanupErrorCodes.AdminAbandoned);
        (await AdminShareEndpoints.HasDurableTransferStateAsync(f.Db, f.ShareId, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Release_for_share_is_idempotent_and_leaves_other_shares_alone()
    {
        using var f = await Fixture.CreateAsync();
        await f.Service.CreateAsync(f.UserId, f.Request("/a.bin", new byte[] { 1 }), CancellationToken.None);

        await f.Service.ReleaseForShareAsync(f.ShareId, CancellationToken.None);
        var second = await f.Service.ReleaseForShareAsync(f.ShareId, CancellationToken.None);

        second.CleanedUp.Should().Be(0);
        second.Abandoned.Should().Be(0);
        (await f.Service.ReleaseForShareAsync(f.ShareId + 999, CancellationToken.None))
            .CleanedUp.Should().Be(0);
    }

    [Fact]
    public async Task Cleanup_stops_retrying_once_the_give_up_window_passes()
    {
        using var f = await Fixture.CreateAsync();
        await f.Service.CreateAsync(f.UserId, f.Request("/a.bin", new byte[] { 1 }), CancellationToken.None);
        f.Router.DeleteTempFailure = new IOException("share is gone");

        // 期限切れ直後は諦めず、5分後の再試行へ送られる。
        f.Clock.Advance(TimeSpan.FromHours(25));
        await f.Service.ExpireSessionsAsync(CancellationToken.None);
        var retrying = await f.Db.UploadSessions.SingleAsync();
        retrying.Status.Should().Be(UploadSessionStatuses.Failed);
        retrying.ErrorCode.Should().Be(TransferCleanupErrorCodes.CleanupRetry);

        // 猶予 (既定7日) を過ぎたら諦めて終端させ、無限リトライを止める。
        f.Clock.Advance(TimeSpan.FromDays(8));
        await f.Service.ExpireSessionsAsync(CancellationToken.None);
        var gaveUp = await f.Db.UploadSessions.SingleAsync();
        gaveUp.Status.Should().Be(UploadSessionStatuses.Cancelled);
        gaveUp.ErrorCode.Should().Be(TransferCleanupErrorCodes.CleanupGaveUp);
        (await AdminShareEndpoints.HasDurableTransferStateAsync(f.Db, f.ShareId, CancellationToken.None))
            .Should().BeFalse();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDb _testDb;
        public AppDbContext Db => _testDb.Db;
        public FakeNodeRouter Router { get; }
        public MutableTimeProvider Clock { get; }
        public UploadSessionService Service { get; }
        public int UserId { get; }
        public int HostId { get; }
        public int ShareId { get; }

        private Fixture(
            TestDb testDb,
            FakeNodeRouter router,
            MutableTimeProvider clock,
            UploadSessionService service,
            int userId,
            int hostId,
            int shareId)
        {
            _testDb = testDb;
            Router = router;
            Clock = clock;
            Service = service;
            UserId = userId;
            HostId = hostId;
            ShareId = shareId;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var testDb = new TestDb();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:MasterKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
                ["TransferV2:SessionLifetimeHours"] = "24",
            }).Build();
            var encryption = new EncryptionService(config);
            var user = new User
            {
                Username = "uploader",
                PasswordHash = "x",
                PasswordChangedAt = DateTime.UtcNow,
                PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
                CreatedAt = DateTime.UtcNow,
            };
            var node = new ExecutionNode
            {
                Name = "direct",
                NodeType = NodeTypes.Direct,
                IsActive = true,
                HealthStatus = HealthStatuses.Healthy,
                CreatedAt = DateTime.UtcNow,
            };
            var host = new CifsHost
            {
                Name = "host",
                HostAddress = "files.test",
                Port = 445,
                CredUsername = "svc",
                CredPasswordEnc = encryption.Encrypt("secret"),
                ExecutionNode = node,
                CreatedAt = DateTime.UtcNow,
            };
            var share = new CifsShare { Host = host, ShareName = "data", DisplayName = "Data" };
            var template = new PermissionTemplate { Name = "rw", CanRead = true, CanWrite = true };
            testDb.Db.AddRange(user, node, host, share, template);
            await testDb.Db.SaveChangesAsync();
            testDb.Db.UserPermissions.Add(new UserPermission
            {
                UserId = user.Id,
                ShareId = share.Id,
                TemplateId = template.Id,
                AllowedPath = "/",
                CreatedAt = DateTime.UtcNow,
            });
            await testDb.Db.SaveChangesAsync();

            var router = new FakeNodeRouter();
            var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
            var service = new UploadSessionService(
                testDb.Db,
                new PermissionService(testDb.Db),
                router,
                encryption,
                config,
                NullLogger<UploadSessionService>.Instance,
                clock);
            return new Fixture(testDb, router, clock, service, user.Id, host.Id, share.Id);
        }

        public CreateUploadSessionRequest Request(
            string path,
            byte[] content,
            string idempotencyKey = "key-1") => new()
            {
                HostId = HostId,
                ShareId = ShareId,
                Path = path,
                TotalSize = content.Length,
                Sha256 = Hash(content),
                IdempotencyKey = idempotencyKey,
                Overwrite = false,
            };

        public void Dispose() => _testDb.Dispose();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class FakeNodeRouter : NodeRouter
    {
        public sealed record FileState(byte[] Bytes, DateTime ModifiedAtUtc);

        public Dictionary<string, FileState> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public int CommitCount { get; private set; }
        public Exception? EnsureTempFailure { get; set; }
        public Exception? DeleteTempFailure { get; set; }
        public Exception? WriteTempStreamFailure { get; set; }
        public int CommitEnteredCount { get; private set; }
        private TaskCompletionSource _ensureTempEntered = NewSignal();
        private TaskCompletionSource _ensureTempRelease = NewSignal();
        private TaskCompletionSource _commitEntered = NewSignal();
        private TaskCompletionSource _commitRelease = NewSignal();
        private TaskCompletionSource _observedTempMetadata = NewSignal();
        private bool _pauseEnsureTemp;
        private bool _pauseCommits;
        private bool _observeNextTempMetadata;

        public FakeNodeRouter()
            : base(new CifsService(new CifsSessionPool()), new FakeForwarder()) { }

        public void PutFile(string path, byte[] bytes, DateTime modifiedAtUtc)
            => Files[PathHelper.NormalizePath(path)] = new FileState(bytes.ToArray(), modifiedAtUtc.ToUniversalTime());

        public void SimulateRenameWithoutAcknowledgement(string tempPath, string targetPath)
        {
            var temp = PathHelper.NormalizePath(tempPath);
            var target = PathHelper.NormalizePath(targetPath);
            Files[target] = Files[temp];
            Files.Remove(temp);
        }

        public void PauseEnsureTemp() => _pauseEnsureTemp = true;
        public Task WaitForEnsureTempEntryAsync() => _ensureTempEntered.Task;
        public void ReleaseEnsureTemp() => _ensureTempRelease.TrySetResult();
        public void PauseCommits() => _pauseCommits = true;
        public Task WaitForCommitEntryAsync() => _commitEntered.Task;
        public void ReleaseCommits() => _commitRelease.TrySetResult();
        public void ObserveNextTempMetadata() => _observeNextTempMetadata = true;
        public Task WaitForObservedTempMetadataAsync() => _observedTempMetadata.Task;

        public override Task<TransferFileMetadata> GetTransferMetadataAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_observeNextTempMetadata && TransferV2Validation.IsReservedTempPath(path))
            {
                _observeNextTempMetadata = false;
                _observedTempMetadata.TrySetResult();
            }
            return Task.FromResult(Files.TryGetValue(PathHelper.NormalizePath(path), out var file)
                ? new TransferFileMetadata(true, TransferFileTypes.File, file.Bytes.LongLength,
                    file.ModifiedAtUtc, false)
                : TransferFileMetadata.Missing);
        }

        public override async Task<TransferFileMetadata> EnsureTempFileAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (EnsureTempFailure is not null)
                throw EnsureTempFailure;
            if (_pauseEnsureTemp)
            {
                _ensureTempEntered.TrySetResult();
                await _ensureTempRelease.Task.WaitAsync(ct);
            }
            var path = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
            if (!Files.ContainsKey(path))
                Files[path] = new FileState(Array.Empty<byte>(), DateTime.UtcNow);
            var file = Files[path];
            return new TransferFileMetadata(true, TransferFileTypes.File,
                file.Bytes.LongLength, file.ModifiedAtUtc, false);
        }

        public override async Task WriteTempStreamAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, Stream input,
            CancellationToken ct)
        {
            if (WriteTempStreamFailure is not null)
                throw WriteTempStreamFailure;
            var path = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);
            Files[path] = new FileState(buffer.ToArray(), DateTime.UtcNow);
        }

        public override Task<TransferChunkWriteResult> WriteTempChunkAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, long offset,
            ReadOnlyMemory<byte> chunk, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
            if (!Files.TryGetValue(path, out var file))
                throw new TransferOffsetMismatchException(offset, 0);
            var overlapLength = file.Bytes.LongLength < offset
                ? 0
                : (int)Math.Min(file.Bytes.LongLength - offset, chunk.Length);
            var existing = overlapLength == 0
                ? ReadOnlySpan<byte>.Empty
                : file.Bytes.AsSpan(checked((int)offset), overlapLength);
            var overlap = TransferV2Validation.ReconcileChunk(
                file.Bytes.LongLength,
                offset,
                existing,
                chunk.Span);
            var next = Math.Max(file.Bytes.LongLength, offset + chunk.Length);
            var output = new byte[checked((int)next)];
            file.Bytes.CopyTo(output, 0);
            chunk.Span[overlap..].CopyTo(output.AsSpan((int)offset + overlap));
            Files[path] = new FileState(output, file.ModifiedAtUtc.AddTicks(1));
            return Task.FromResult(new TransferChunkWriteResult(
                offset, chunk.Length, chunk.Length - overlap, next, overlap == chunk.Length));
        }

        public override Task<TransferSha256Result> ComputeSha256Async(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var file = Files[PathHelper.NormalizePath(path)];
            return Task.FromResult(new TransferSha256Result(
                "SHA-256", Hash(file.Bytes), file.Bytes.LongLength));
        }

        public override async Task CommitTempAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, string targetPath,
            bool replaceIfExists, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CommitEnteredCount++;
            _commitEntered.TrySetResult();
            if (_pauseCommits)
                await _commitRelease.Task.WaitAsync(ct);
            var paths = TransferV2Validation.ValidateCommitPaths(tempPath, targetPath);
            if (!replaceIfExists && Files.ContainsKey(paths.TargetPath))
                throw new IOException("target exists");
            Files[paths.TargetPath] = Files[paths.TempPath];
            Files.Remove(paths.TempPath);
            CommitCount++;
        }

        public override Task DeleteTempAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (DeleteTempFailure is not null)
                return Task.FromException(DeleteTempFailure);
            Files.Remove(TransferV2Validation.NormalizeAndValidateTempPath(tempPath));
            return Task.CompletedTask;
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeForwarder : AgentForwarder
    {
        public FakeForwarder()
            : base(new EmptyFactory(), new ConfigurationBuilder().Build(),
                NullLogger<AgentForwarder>.Instance)
        { }
    }

    private sealed class EmptyFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
