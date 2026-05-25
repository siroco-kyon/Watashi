using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

/// <summary>
/// PermissionServiceTests を補完: 複数権限がある場合の最も寛容な権限の選択、
/// ロケーション集計、別ユーザーの権限混入なしの確認など。
/// </summary>
public class PermissionServiceExtraTests
{
    private record SeedResult(TestDb Db, int UserId, int ShareId, int OtherUserId);

    private static async Task<SeedResult> SeedTwoUsersAsync()
    {
        var db = new TestDb();
        var u1 = new User { Username = "u1", PasswordHash = "x", PasswordChangedAt = DateTime.UtcNow, PasswordExpiresAt = DateTime.UtcNow.AddDays(30), CreatedAt = DateTime.UtcNow };
        var u2 = new User { Username = "u2", PasswordHash = "x", PasswordChangedAt = DateTime.UtcNow, PasswordExpiresAt = DateTime.UtcNow.AddDays(30), CreatedAt = DateTime.UtcNow };
        var n = new ExecutionNode { Name = "n", NodeType = NodeTypes.Direct, HealthStatus = HealthStatuses.Healthy, CreatedAt = DateTime.UtcNow };
        var h = new CifsHost { Name = "h", HostAddress = "1.2.3.4", CredUsername = "user", CredPasswordEnc = new byte[] { 0 }, ExecutionNode = n, CreatedAt = DateTime.UtcNow };
        var s = new CifsShare { ShareName = "docs", DisplayName = "Docs", Host = h };
        var tro = new PermissionTemplate { Name = "RO", CanRead = true };
        var trw = new PermissionTemplate { Name = "RW", CanRead = true, CanWrite = true };
        db.Db.Users.AddRange(u1, u2);
        db.Db.ExecutionNodes.Add(n);
        db.Db.CifsHosts.Add(h);
        db.Db.CifsShares.Add(s);
        db.Db.PermissionTemplates.AddRange(tro, trw);
        await db.Db.SaveChangesAsync();
        return new SeedResult(db, u1.Id, s.Id, u2.Id);
    }

    [Fact]
    public async Task Multiple_permissions_use_first_matching_entry()
    {
        var seed = await SeedTwoUsersAsync();
        using var db = seed.Db;
        var trw = db.Db.PermissionTemplates.First(t => t.Name == "RW");
        var tro = db.Db.PermissionTemplates.First(t => t.Name == "RO");
        db.Db.UserPermissions.AddRange(
            new UserPermission { UserId = seed.UserId, ShareId = seed.ShareId, TemplateId = tro.Id, AllowedPath = "/dept-A", CreatedAt = DateTime.UtcNow },
            new UserPermission { UserId = seed.UserId, ShareId = seed.ShareId, TemplateId = trw.Id, AllowedPath = "/dept-A/special", CreatedAt = DateTime.UtcNow });
        await db.Db.SaveChangesAsync();

        var svc = new PermissionService(db.Db);
        // 最初に許可されたエントリで OK を返す。順序は EF の取得順で安定化されないため、
        // どちらかが許可していれば true になることを確認。
        (await svc.CanPerformAsync(seed.UserId, seed.ShareId, "/dept-A/special/file.txt", Operations.Write))
            .allowed.Should().BeTrue();
        // 上位 (special 外) には Write 権限が無い: ヒットするエントリは RO のみで Write は不可。
        (await svc.CanPerformAsync(seed.UserId, seed.ShareId, "/dept-A/other/file.txt", Operations.Write))
            .allowed.Should().BeFalse();
    }

    [Fact]
    public async Task GetUserLocations_returns_only_caller_user_entries()
    {
        var seed = await SeedTwoUsersAsync();
        using var db = seed.Db;
        var tro = db.Db.PermissionTemplates.First(t => t.Name == "RO");
        db.Db.UserPermissions.AddRange(
            new UserPermission { UserId = seed.UserId, ShareId = seed.ShareId, TemplateId = tro.Id, AllowedPath = "/dept-A", CreatedAt = DateTime.UtcNow },
            new UserPermission { UserId = seed.OtherUserId, ShareId = seed.ShareId, TemplateId = tro.Id, AllowedPath = "/dept-B", CreatedAt = DateTime.UtcNow });
        await db.Db.SaveChangesAsync();

        var svc = new PermissionService(db.Db);
        var u1Loc = await svc.GetUserLocationsAsync(seed.UserId);
        u1Loc.Should().HaveCount(1);
        u1Loc[0].Path.Should().Be("/dept-A");

        var u2Loc = await svc.GetUserLocationsAsync(seed.OtherUserId);
        u2Loc.Should().HaveCount(1);
        u2Loc[0].Path.Should().Be("/dept-B");
    }

    [Fact]
    public async Task GetUserLocations_filters_by_host_and_share()
    {
        var seed = await SeedTwoUsersAsync();
        using var db = seed.Db;
        var tro = db.Db.PermissionTemplates.First(t => t.Name == "RO");
        db.Db.UserPermissions.Add(new UserPermission
        {
            UserId = seed.UserId, ShareId = seed.ShareId, TemplateId = tro.Id, AllowedPath = "/dept-A", CreatedAt = DateTime.UtcNow,
        });
        await db.Db.SaveChangesAsync();

        var svc = new PermissionService(db.Db);
        // ShareId 不一致では結果が空になる。
        var none = await svc.GetUserLocationsAsync(seed.UserId, shareId: seed.ShareId + 999);
        none.Should().BeEmpty();
        // 一致するときは取得できる。
        var ok = await svc.GetUserLocationsAsync(seed.UserId, shareId: seed.ShareId);
        ok.Should().HaveCount(1);
    }

    [Fact]
    public async Task CanPerformAsync_returns_permissionId_used_for_grant()
    {
        var seed = await SeedTwoUsersAsync();
        using var db = seed.Db;
        var tro = db.Db.PermissionTemplates.First(t => t.Name == "RO");
        var p = new UserPermission { UserId = seed.UserId, ShareId = seed.ShareId, TemplateId = tro.Id, AllowedPath = "/dept-A", CreatedAt = DateTime.UtcNow };
        db.Db.UserPermissions.Add(p);
        await db.Db.SaveChangesAsync();

        var svc = new PermissionService(db.Db);
        var (ok, pid) = await svc.CanPerformAsync(seed.UserId, seed.ShareId, "/dept-A/file.txt", Operations.Read);
        ok.Should().BeTrue();
        pid.Should().Be(p.Id);
    }
}
