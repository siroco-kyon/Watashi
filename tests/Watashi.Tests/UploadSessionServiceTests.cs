using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Watashi.Server.Data;
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

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

        public override Task<TransferFileMetadata> GetTransferMetadataAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Files.TryGetValue(PathHelper.NormalizePath(path), out var file)
                ? new TransferFileMetadata(true, TransferFileTypes.File, file.Bytes.LongLength,
                    file.ModifiedAtUtc, false)
                : TransferFileMetadata.Missing);
        }

        public override Task<TransferFileMetadata> EnsureTempFileAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
            if (!Files.ContainsKey(path))
                Files[path] = new FileState(Array.Empty<byte>(), DateTime.UtcNow);
            var file = Files[path];
            return Task.FromResult(new TransferFileMetadata(true, TransferFileTypes.File,
                file.Bytes.LongLength, file.ModifiedAtUtc, false));
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

        public override Task CommitTempAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, string targetPath,
            bool replaceIfExists, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var paths = TransferV2Validation.ValidateCommitPaths(tempPath, targetPath);
            if (!replaceIfExists && Files.ContainsKey(paths.TargetPath))
                throw new IOException("target exists");
            Files[paths.TargetPath] = Files[paths.TempPath];
            Files.Remove(paths.TempPath);
            CommitCount++;
            return Task.CompletedTask;
        }

        public override Task DeleteTempAsync(
            ExecutionNode node, CifsConnectionInfo info, string tempPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Files.Remove(TransferV2Validation.NormalizeAndValidateTempPath(tempPath));
            return Task.CompletedTask;
        }
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
