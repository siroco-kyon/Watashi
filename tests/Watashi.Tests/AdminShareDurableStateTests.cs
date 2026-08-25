using FluentAssertions;
using Watashi.Server.Endpoints;
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
