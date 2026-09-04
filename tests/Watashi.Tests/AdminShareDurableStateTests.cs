using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Endpoints;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public sealed class AdminShareDurableStateTests
{
    [Theory]
    [InlineData(UploadSessionStatuses.Active, true)]
    [InlineData(UploadSessionStatuses.Committing, true)]
    [InlineData(UploadSessionStatuses.Failed, true)]
    [InlineData(UploadSessionStatuses.Completed, false)]
    [InlineData(UploadSessionStatuses.Cancelled, false)]
    [InlineData(UploadSessionStatuses.Expired, false)]
    public async Task Upload_state_blocks_physical_share_changes_only_while_cleanup_is_needed(
        string status,
        bool expected)
    {
        using var testDb = new TestDb();
        var (user, share) = await SeedShareAsync(testDb);
        testDb.Db.UploadSessions.Add(new UploadSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            HostId = share.HostId,
            ShareId = share.Id,
            TargetPath = "/a.bin",
            TempPath = $"/.watashi-upload-{Guid.NewGuid():N}.tmp",
            IdempotencyKeyHash = new string('a', 64),
            TotalSize = 1,
            ExpectedSha256 = new string('b', 64),
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await testDb.Db.SaveChangesAsync();

        var actual = await AdminShareEndpoints.HasDurableTransferStateAsync(
            testDb.Db, share.Id, CancellationToken.None);

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(RemoteTrashStatuses.Trashing, true)]
    [InlineData(RemoteTrashStatuses.Active, true)]
    [InlineData(RemoteTrashStatuses.Restoring, true)]
    [InlineData(RemoteTrashStatuses.Purging, true)]
    [InlineData(RemoteTrashStatuses.Failed, true)]
    [InlineData(RemoteTrashStatuses.Restored, false)]
    [InlineData(RemoteTrashStatuses.Purged, false)]
    public async Task Trash_state_blocks_physical_share_changes_until_item_is_terminal(
        string status,
        bool expected)
    {
        using var testDb = new TestDb();
        var (user, share) = await SeedShareAsync(testDb);
        testDb.Db.RemoteTrashEntries.Add(new RemoteTrashEntry
        {
            Id = Guid.NewGuid(),
            HostId = share.HostId,
            ShareId = share.Id,
            DeletedByUserId = user.Id,
            DeletedByUsername = user.Username,
            OriginalPath = "/a.bin",
            TrashPath = $"/.watashi-trash/{Guid.NewGuid():N}",
            ItemType = "file",
            Status = status,
            DeletedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await testDb.Db.SaveChangesAsync();

        var actual = await AdminShareEndpoints.HasDurableTransferStateAsync(
            testDb.Db, share.Id, CancellationToken.None);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task Durable_state_lists_the_blocking_rows_and_flags_the_stuck_ones()
    {
        using var testDb = new TestDb();
        var (user, share) = await SeedShareAsync(testDb);
        testDb.Db.UploadSessions.AddRange(
            NewSession(user, share, UploadSessionStatuses.Active, errorCode: null),
            NewSession(user, share, UploadSessionStatuses.Failed,
                errorCode: TransferCleanupErrorCodes.CleanupRetry),
            // 終端済みはブロックしないので出さない。
            NewSession(user, share, UploadSessionStatuses.Completed, errorCode: null));
        await testDb.Db.SaveChangesAsync();

        var state = await AdminShareEndpoints.ReadDurableStateAsync(
            testDb.Db, share.Id, CancellationToken.None);

        state.ShareId.Should().Be(share.Id);
        state.Uploads.Should().HaveCount(2);
        state.Uploads.Should().OnlyContain(u => u.Username == user.Username);
        state.BlocksPhysicalChange.Should().BeTrue();
        state.StuckCount.Should().Be(1, "cleanup_retry は待っても解消しないため区別する");
    }

    [Fact]
    public async Task Durable_state_is_empty_when_nothing_blocks_the_share()
    {
        using var testDb = new TestDb();
        var (_, share) = await SeedShareAsync(testDb);

        var state = await AdminShareEndpoints.ReadDurableStateAsync(
            testDb.Db, share.Id, CancellationToken.None);

        state.Uploads.Should().BeEmpty();
        state.Trash.Should().BeEmpty();
        state.BlocksPhysicalChange.Should().BeFalse();
        state.StuckCount.Should().Be(0);
    }

    [Fact]
    public async Task Release_finds_sessions_that_were_left_behind_by_a_failed_cleanup()
    {
        using var testDb = new TestDb();
        var (user, share) = await SeedShareAsync(testDb);
        testDb.Db.UploadSessions.Add(NewSession(user, share, UploadSessionStatuses.Failed,
            errorCode: TransferCleanupErrorCodes.CleanupRetry));
        await testDb.Db.SaveChangesAsync();

        var state = await AdminShareEndpoints.ReadDurableStateAsync(
            testDb.Db, share.Id, CancellationToken.None);
        state.Uploads.Should().HaveCount(1, "参照側は対象として見えている");

        var pending = await testDb.Db.UploadSessions.AsNoTracking()
            .Where(x => x.ShareId == share.Id &&
                        (x.Status == UploadSessionStatuses.Active ||
                         x.Status == UploadSessionStatuses.Committing ||
                         x.Status == UploadSessionStatuses.Failed))
            .Select(x => x.Id)
            .ToListAsync();
        pending.Should().HaveCount(1, "解除側と同じ条件でも同じ行が選ばれる");
    }

    private static UploadSession NewSession(
        User user, CifsShare share, string status, string? errorCode) => new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            HostId = share.HostId,
            ShareId = share.Id,
            TargetPath = "/a.bin",
            TempPath = $"/.watashi-upload-{Guid.NewGuid():N}.tmp",
            IdempotencyKeyHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            TotalSize = 1,
            ExpectedSha256 = new string('b', 64),
            Status = status,
            ErrorCode = errorCode,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };

    private static async Task<(User User, CifsShare Share)> SeedShareAsync(TestDb testDb)
    {
        var user = new User
        {
            Username = $"u-{Guid.NewGuid():N}",
            PasswordHash = "x",
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedAt = DateTime.UtcNow,
        };
        var node = new ExecutionNode
        {
            Name = "direct",
            NodeType = "Direct",
            HealthStatus = "Healthy",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        var host = new CifsHost
        {
            Name = "host",
            HostAddress = "files.test",
            Port = 445,
            CredUsername = "svc",
            CredPasswordEnc = new byte[] { 1 },
            ExecutionNode = node,
            CreatedAt = DateTime.UtcNow,
        };
        var share = new CifsShare
        {
            Host = host,
            ShareName = "data",
            DisplayName = "Data",
        };
        testDb.Db.AddRange(user, node, host, share);
        await testDb.Db.SaveChangesAsync();
        return (user, share);
    }
}
