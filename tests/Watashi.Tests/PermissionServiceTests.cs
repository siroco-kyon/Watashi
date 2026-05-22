using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

public class PermissionServiceTests
{
    private static async Task<(TestDb db, int userId, int shareId)> SeedAsync(
        bool canRead = true, bool canWrite = false, bool canDelete = false, bool canRename = false,
        string allowedPath = "/dept-A")
    {
        var db = new TestDb();
        var u = new User { Username = "u1", PasswordHash = "x", PasswordChangedAt = DateTime.UtcNow, PasswordExpiresAt = DateTime.UtcNow.AddDays(30), CreatedAt = DateTime.UtcNow };
        var n = new ExecutionNode { Name = "Direct", NodeType = NodeTypes.Direct, HealthStatus = HealthStatuses.Healthy, CreatedAt = DateTime.UtcNow };
        var h = new CifsHost { Name = "host", HostAddress = "1.2.3.4", CredUsername = "user", CredPasswordEnc = new byte[] { 0 }, ExecutionNode = n, CreatedAt = DateTime.UtcNow };
        var s = new CifsShare { ShareName = "docs", DisplayName = "Docs", Host = h };
        var t = new PermissionTemplate { Name = "T", CanRead = canRead, CanWrite = canWrite, CanDelete = canDelete, CanRename = canRename };
        db.Db.Users.Add(u); db.Db.ExecutionNodes.Add(n); db.Db.CifsHosts.Add(h); db.Db.CifsShares.Add(s); db.Db.PermissionTemplates.Add(t);
        await db.Db.SaveChangesAsync();
        var perm = new UserPermission { UserId = u.Id, ShareId = s.Id, TemplateId = t.Id, AllowedPath = allowedPath, CreatedAt = DateTime.UtcNow };
        db.Db.UserPermissions.Add(perm);
        await db.Db.SaveChangesAsync();
        return (db, u.Id, s.Id);
    }

    [Fact]
    public async Task Read_inside_allowed_path_is_allowed()
    {
        var (db, uid, sid) = await SeedAsync(canRead: true);
        using var _ = db;
        var svc = new PermissionService(db.Db);
        var (allowed, pid) = await svc.CanPerformAsync(uid, sid, "/dept-A/sub/file.txt", Operations.Read);
        allowed.Should().BeTrue();
        pid.Should().NotBeNull();
    }

    [Fact]
    public async Task Write_when_template_does_not_allow_is_denied()
    {
        var (db, uid, sid) = await SeedAsync(canRead: true, canWrite: false);
        using var _ = db;
        var svc = new PermissionService(db.Db);
        var (allowed, _) = await svc.CanPerformAsync(uid, sid, "/dept-A/file.txt", Operations.Write);
        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Out_of_subpath_is_denied()
    {
        var (db, uid, sid) = await SeedAsync(canRead: true);
        using var _ = db;
        var svc = new PermissionService(db.Db);
        var (allowed, _) = await svc.CanPerformAsync(uid, sid, "/dept-B/file.txt", Operations.Read);
        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Traversal_attempt_is_blocked()
    {
        var (db, uid, sid) = await SeedAsync(canRead: true);
        using var _ = db;
        var svc = new PermissionService(db.Db);
        var (allowed, _) = await svc.CanPerformAsync(uid, sid, "/dept-A/../../etc/passwd", Operations.Read);
        allowed.Should().BeFalse(); // 正規化後は /etc/passwd になり、許可範囲外
    }

    [Fact]
    public async Task Root_allowed_grants_everything()
    {
        var (db, uid, sid) = await SeedAsync(canRead: true, canWrite: true, canDelete: true, canRename: true, allowedPath: "/");
        using var _ = db;
        var svc = new PermissionService(db.Db);
        (await svc.CanPerformAsync(uid, sid, "/anywhere/at/all.bin", Operations.Read)).allowed.Should().BeTrue();
        (await svc.CanPerformAsync(uid, sid, "/anywhere/at/all.bin", Operations.Delete)).allowed.Should().BeTrue();
    }

    [Fact]
    public async Task IsPermissionRoot_matches_only_exact_path()
    {
        var (db, uid, sid) = await SeedAsync(canRead: true, allowedPath: "/dept-A");
        using var _ = db;
        var svc = new PermissionService(db.Db);
        (await svc.IsPermissionRootAsync(uid, sid, "/dept-A")).Should().BeTrue();
        (await svc.IsPermissionRootAsync(uid, sid, "/dept-A/")).Should().BeTrue();
        (await svc.IsPermissionRootAsync(uid, sid, "/dept-A/sub")).Should().BeFalse();
        (await svc.IsPermissionRootAsync(uid, sid, "/dept-B")).Should().BeFalse();
    }
}
