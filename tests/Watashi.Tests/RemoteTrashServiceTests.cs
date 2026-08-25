using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public sealed class RemoteTrashServiceTests
{
    [Fact]
    public async Task Trash_moves_item_persists_retention_and_retry_is_idempotent()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PutFile("/docs/a.txt", 4);

        var first = await f.TrashAsync("/docs/a.txt");
        var retry = await f.TrashAsync("/docs/a.txt");

        first.Changed.Should().BeTrue();
        retry.Changed.Should().BeFalse();
        retry.Entry.Id.Should().Be(first.Entry.Id);
        f.Router.Files.Should().NotContainKey("/docs/a.txt")
            .And.ContainKey(RemoteTrashPathPolicy.BuildItemPath(first.Entry.Id));
        first.Entry.ExpiresAt.Should().Be(first.Entry.DeletedAt.AddDays(30));
        f.Router.MoveCount.Should().Be(1);
    }

    [Fact]
    public async Task Listing_is_limited_to_deleter_while_admin_sees_all()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PutFile("/docs/a.txt", 7);
        await f.TrashAsync("/docs/a.txt");

        var owner = await f.Service.ListAsync(
            f.UserId, isAdmin: false, null, null, 1, 100, CancellationToken.None);
        var another = await f.Service.ListAsync(
            f.OtherUserId, isAdmin: false, null, null, 1, 100, CancellationToken.None);
        var admin = await f.Service.ListAsync(
            f.OtherUserId, isAdmin: true, null, null, 1, 100, CancellationToken.None);

        owner.TotalCount.Should().Be(1);
        owner.TotalBytes.Should().Be(7);
        another.TotalCount.Should().Be(0);
        admin.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Capacity_limit_rejects_before_rename()
    {
        using var f = await Fixture.CreateAsync();
        f.Db.SystemSettings.Add(new SystemSetting
        {
            Key = SettingKeys.TrashCapacityBytes,
            Value = "3",
            UpdatedAt = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();
        f.Router.PutFile("/docs/large.bin", 4);

        var act = async () => await f.TrashAsync("/docs/large.bin");

        await act.Should().ThrowAsync<RemoteTrashException>()
            .Where(e => e.StatusCode == 507 && e.Code == "trash_capacity_exceeded");
        f.Router.Files.Should().ContainKey("/docs/large.bin");
        f.Router.MoveCount.Should().Be(0);
    }

    [Fact]
    public async Task Restore_fail_and_rename_collision_policies_are_enforced()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PutFile("/docs/a.txt", 4);
        var trashed = await f.TrashAsync("/docs/a.txt");
        f.Router.PutFile("/docs/a.txt", 10);

        var fail = async () => await f.Service.RestoreAsync(
            f.UserId, isAdmin: false, trashed.Entry.Id,
            TrashCollisionPolicies.Fail, CancellationToken.None);
        await fail.Should().ThrowAsync<RemoteTrashException>()
            .Where(e => e.Code == "target_exists" && e.StatusCode == 409);

        var restored = await f.Service.RestoreAsync(
            f.UserId, isAdmin: false, trashed.Entry.Id,
            TrashCollisionPolicies.Rename, CancellationToken.None);

        restored.TargetPath.Should().Be("/docs/a (restored 1).txt");
        f.Router.Files.Should().ContainKey("/docs/a.txt")
            .And.ContainKey("/docs/a (restored 1).txt")
            .And.NotContainKey(trashed.Entry.Id.ToString());
        (await f.Db.RemoteTrashEntries.SingleAsync()).Status.Should().Be(RemoteTrashStatuses.Restored);
    }

    [Fact]
    public async Task Restore_rechecks_permission_before_disclosing_target_collision()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PutFile("/docs/a.txt", 4);
        var trashed = await f.TrashAsync("/docs/a.txt");
        f.Router.PutFile("/docs/a.txt", 99);
        f.Db.UserPermissions.RemoveRange(f.Db.UserPermissions.Where(p => p.UserId == f.UserId));
        await f.Db.SaveChangesAsync();

        var act = async () => await f.Service.RestoreAsync(
            f.UserId, isAdmin: false, trashed.Entry.Id,
            TrashCollisionPolicies.Fail, CancellationToken.None);

        await act.Should().ThrowAsync<RemoteTrashException>()
            .Where(e => e.Code == "restore_permission_denied" && e.StatusCode == 403);
    }

    [Fact]
    public async Task Overwrite_and_immediate_purge_require_admin()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PutFile("/docs/a.txt", 4);
        var trashed = await f.TrashAsync("/docs/a.txt");

        var overwrite = async () => await f.Service.RestoreAsync(
            f.UserId, isAdmin: false, trashed.Entry.Id,
            TrashCollisionPolicies.Overwrite, CancellationToken.None);
        var purge = async () => await f.Service.PurgeAsync(
            f.UserId, isAdmin: false, trashed.Entry.Id, CancellationToken.None);

        await overwrite.Should().ThrowAsync<RemoteTrashException>()
            .Where(e => e.Code == "overwrite_requires_admin");
        await purge.Should().ThrowAsync<RemoteTrashException>()
            .Where(e => e.Code == "purge_requires_admin");

        var adminPurge = await f.Service.PurgeAsync(
            f.OtherUserId, isAdmin: true, trashed.Entry.Id, CancellationToken.None);
        adminPurge.Changed.Should().BeTrue();
        f.Router.Files.Should().NotContainKey(trashed.TargetPath!);
    }

    [Fact]
    public async Task Expired_item_is_purged_and_system_audit_contains_source()
    {
        using var f = await Fixture.CreateAsync();
        f.Db.SystemSettings.Add(new SystemSetting
        {
            Key = SettingKeys.TrashRetentionDays,
            Value = "1",
            UpdatedAt = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();
        f.Router.PutFile("/docs/old.txt", 12);
        var trashed = await f.TrashAsync("/docs/old.txt");
        f.Clock.Advance(TimeSpan.FromDays(2));

        (await f.Service.PurgeExpiredAsync(CancellationToken.None)).Should().Be(1);

        var entry = await f.Db.RemoteTrashEntries.SingleAsync();
        entry.Status.Should().Be(RemoteTrashStatuses.Purged);
        var audit = await f.Db.AuditLogs.SingleAsync(a => a.Operation == Operations.Purge);
        audit.Username.Should().Be("(system)");
        audit.Path.Should().Be(trashed.TargetPath);
        audit.Result.Should().Be(AuditResults.Success);
    }

    [Fact]
    public async Task Reparse_item_is_rejected_without_creating_entry()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PutFile("/docs/link", 0, reparse: true);

        var act = async () => await f.TrashAsync("/docs/link");

        await act.Should().ThrowAsync<RemoteTrashException>()
            .Where(e => e.Code == "reparse_point_rejected");
        (await f.Db.RemoteTrashEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Expired_purge_failure_is_deferred_so_following_entries_can_progress()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PutFile("/old.bin", 10);
        await f.TrashAsync("/old.bin");
        f.Clock.Advance(TimeSpan.FromDays(RemoteTrashService.DefaultRetentionDays + 1));
        f.Router.PurgeFailure = new IOException("SMB unavailable");

        (await f.Service.PurgeExpiredAsync(CancellationToken.None)).Should().Be(0);

        var entry = await f.Db.RemoteTrashEntries.SingleAsync();
        entry.Status.Should().Be(RemoteTrashStatuses.Active);
        entry.ErrorCode.Should().Be("purge_retry");
        entry.ExpiresAt.Should().Be(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(5));
    }

    [Fact]
    public async Task Legacy_failed_entry_is_reconciled_by_backend_janitor_without_reenabling_api()
    {
        using var f = await Fixture.CreateAsync();
        f.Router.PutFile("/legacy.bin", 10);
        var trashed = await f.TrashAsync("/legacy.bin");
        var entry = await f.Db.RemoteTrashEntries.SingleAsync();
        entry.Status = RemoteTrashStatuses.Failed;
        entry.ExpiresAt = f.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        await f.Db.SaveChangesAsync();

        (await f.Service.PurgeExpiredAsync(CancellationToken.None)).Should().Be(1);

        entry.Status.Should().Be(RemoteTrashStatuses.Purged);
        f.Router.Files.Should().NotContainKey(trashed.TargetPath!);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDb _testDb;
        public AppDbContext Db => _testDb.Db;
        public FakeNodeRouter Router { get; }
        public MutableTimeProvider Clock { get; }
        public RemoteTrashService Service { get; }
        public User User { get; }
        public ExecutionNode Node { get; }
        public CifsConnectionInfo Info { get; }
        public int UserId => User.Id;
        public int OtherUserId { get; }
        public int HostId { get; }
        public int ShareId { get; }

        private Fixture(
            TestDb testDb,
            FakeNodeRouter router,
            MutableTimeProvider clock,
            RemoteTrashService service,
            User user,
            int otherUserId,
            ExecutionNode node,
            CifsConnectionInfo info,
            int hostId,
            int shareId)
        {
            _testDb = testDb;
            Router = router;
            Clock = clock;
            Service = service;
            User = user;
            OtherUserId = otherUserId;
            Node = node;
            Info = info;
            HostId = hostId;
            ShareId = shareId;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var testDb = new TestDb();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:MasterKey"] = Convert.ToBase64String(
                    Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            }).Build();
            var encryption = new EncryptionService(config);
            var user = CreateUser("deleter");
            var other = CreateUser("other");
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
            var template = new PermissionTemplate
            {
                Name = "rw-delete",
                CanRead = true,
                CanWrite = true,
                CanDelete = true,
            };
            testDb.Db.AddRange(user, other, node, host, share, template);
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
            var service = new RemoteTrashService(
                testDb.Db,
                new PermissionService(testDb.Db),
                router,
                encryption,
                new AuditLogService(testDb.Db),
                NullLogger<RemoteTrashService>.Instance,
                clock);
            return new Fixture(testDb, router, clock, service, user, other.Id, node,
                new CifsConnectionInfo("files.test", 445, "svc", "secret", "data"),
                host.Id, share.Id);
        }

        public Task<RemoteTrashActionResult> TrashAsync(string path)
            => Service.TrashAsync(UserId, User.Username, HostId, ShareId, path,
                Node, Info, permissionId: 1, CancellationToken.None);

        private static User CreateUser(string username) => new()
        {
            Username = username,
            PasswordHash = "x",
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
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
        public sealed record ItemState(string Type, long Size, DateTime ModifiedAtUtc, bool Reparse);
        public Dictionary<string, ItemState> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int MoveCount { get; private set; }
        public Exception? PurgeFailure { get; set; }

        public FakeNodeRouter()
            : base(new CifsService(new CifsSessionPool()), new FakeForwarder()) { }

        public void PutFile(string path, long size, bool reparse = false)
            => Files[PathHelper.NormalizePath(path)] = new ItemState(
                TransferFileTypes.File, size, DateTime.UtcNow, reparse);

        public override Task<TransferFileMetadata> GetTransferMetadataAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Files.TryGetValue(PathHelper.NormalizePath(path), out var item)
                ? new TransferFileMetadata(true, item.Type,
                    item.Type == TransferFileTypes.File ? item.Size : null,
                    item.ModifiedAtUtc, item.Reparse)
                : TransferFileMetadata.Missing);
        }

        public override Task<RemoteTrashItemMetadata> InspectForTrashAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = RemoteTrashPathPolicy.NormalizeUserPath(path);
            if (!Files.TryGetValue(normalized, out var item))
                throw new FileNotFoundException("missing", normalized);
            if (item.Reparse) throw new TransferReparsePointException(normalized);
            return Task.FromResult(new RemoteTrashItemMetadata(item.Type, item.Size, item.ModifiedAtUtc));
        }

        public override Task MoveToTrashAsync(
            ExecutionNode node, CifsConnectionInfo info, string sourcePath, string trashPath,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var source = RemoteTrashPathPolicy.NormalizeUserPath(sourcePath);
            var target = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
            if (Files.ContainsKey(target)) throw new IOException("target exists");
            Files[target] = Files[source];
            Files.Remove(source);
            MoveCount++;
            return Task.CompletedTask;
        }

        public override Task RestoreFromTrashAsync(
            ExecutionNode node, CifsConnectionInfo info, string trashPath, string targetPath,
            bool replaceIfExists, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var source = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
            var target = RemoteTrashPathPolicy.NormalizeUserPath(targetPath);
            if (!replaceIfExists && Files.ContainsKey(target)) throw new IOException("target exists");
            Files[target] = Files[source];
            Files.Remove(source);
            return Task.CompletedTask;
        }

        public override Task PurgeTrashItemAsync(
            ExecutionNode node, CifsConnectionInfo info, string trashPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (PurgeFailure is not null)
                return Task.FromException(PurgeFailure);
            Files.Remove(RemoteTrashPathPolicy.ValidateItemPath(trashPath));
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
