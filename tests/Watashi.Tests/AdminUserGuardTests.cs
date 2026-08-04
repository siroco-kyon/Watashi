using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

public class AdminUserGuardTests
{
    private static User MkUser(string name, bool admin, bool locked = false, bool pending = false) => new()
    {
        Username = name,
        PasswordHash = "x",
        IsAdmin = admin,
        IsLocked = locked,
        IsPasswordSetupPending = pending,
        PasswordChangedAt = DateTime.UtcNow,
        PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
        CreatedAt = DateTime.UtcNow,
    };

    // ===== CanDeleteAsync =====

    [Fact]
    public async Task CanDelete_rejects_self_deletion()
    {
        using var db = new TestDb();
        var alice = MkUser("alice", admin: true);
        db.Db.Users.Add(alice);
        await db.Db.SaveChangesAsync();

        var d = await AdminUserGuard.CanDeleteAsync(db.Db, alice.Id, alice.Id, alice.IsAdmin);
        d.Should().Be(AdminUserGuard.Decision.SelfTarget);
    }

    [Fact]
    public async Task CanDelete_rejects_last_active_admin()
    {
        using var db = new TestDb();
        var lone = MkUser("only-admin", admin: true);
        var nonAdmin = MkUser("guest", admin: false);
        db.Db.Users.AddRange(lone, nonAdmin);
        await db.Db.SaveChangesAsync();

        // 別人 (例: スーパー管理者の別アカウント) が lone-admin を消そうとした場合でも、
        // 他に有効な管理者が無いなら拒否する。
        var d = await AdminUserGuard.CanDeleteAsync(db.Db, actorUserId: 999, lone.Id, lone.IsAdmin);
        d.Should().Be(AdminUserGuard.Decision.LastActiveAdmin);
    }

    [Fact]
    public async Task CanDelete_allows_when_other_active_admin_exists()
    {
        using var db = new TestDb();
        var a = MkUser("a", admin: true);
        var b = MkUser("b", admin: true);
        db.Db.Users.AddRange(a, b);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDeleteAsync(db.Db, actorUserId: 999, a.Id, a.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.Allow);
    }

    [Fact]
    public async Task CanDelete_locked_admin_does_not_count_as_active()
    {
        // ロック中の admin は事実上ログイン不可なので「アクティブな他管理者」にカウントしない。
        using var db = new TestDb();
        var locked = MkUser("locked", admin: true, locked: true);
        var lone = MkUser("only", admin: true);
        db.Db.Users.AddRange(locked, lone);
        await db.Db.SaveChangesAsync();

        var d = await AdminUserGuard.CanDeleteAsync(db.Db, actorUserId: 999, lone.Id, lone.IsAdmin);
        d.Should().Be(AdminUserGuard.Decision.LastActiveAdmin);
    }

    [Fact]
    public async Task CanDelete_pending_setup_admin_does_not_count_as_active()
    {
        // 初回パスワード設定待ちの admin はまだログインできないので「アクティブな他管理者」ではない。
        // これを数えてしまうと、実際に使える最後の管理者を削除できてしまう。
        using var db = new TestDb();
        var pending = MkUser("pending-admin", admin: true, pending: true);
        var lone = MkUser("only", admin: true);
        db.Db.Users.AddRange(pending, lone);
        await db.Db.SaveChangesAsync();

        var d = await AdminUserGuard.CanDeleteAsync(db.Db, actorUserId: 999, lone.Id, lone.IsAdmin);
        d.Should().Be(AdminUserGuard.Decision.LastActiveAdmin);
    }

    [Fact]
    public async Task CanDelete_allows_deleting_a_pending_setup_admin_itself()
    {
        // 逆に、未設定 admin 自身の削除は他に有効な管理者が居れば通す。
        using var db = new TestDb();
        var pending = MkUser("pending-admin", admin: true, pending: true);
        var active = MkUser("active", admin: true);
        db.Db.Users.AddRange(pending, active);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDeleteAsync(db.Db, actorUserId: active.Id, pending.Id, pending.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.Allow);
    }

    [Fact]
    public async Task CanDelete_allows_deleting_non_admin_freely()
    {
        using var db = new TestDb();
        var a = MkUser("a", admin: true);
        var g = MkUser("guest", admin: false);
        db.Db.Users.AddRange(a, g);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDeleteAsync(db.Db, actorUserId: a.Id, g.Id, g.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.Allow);
    }

    // ===== CanDemoteAsync =====

    [Fact]
    public async Task CanDemote_rejects_self_demotion()
    {
        using var db = new TestDb();
        var alice = MkUser("alice", admin: true);
        var bob = MkUser("bob", admin: true);
        db.Db.Users.AddRange(alice, bob);
        await db.Db.SaveChangesAsync();

        var d = await AdminUserGuard.CanDemoteAsync(db.Db, alice.Id, alice.Id, currentIsAdmin: true, newIsAdmin: false);
        d.Should().Be(AdminUserGuard.Decision.SelfTarget);
    }

    [Fact]
    public async Task CanDemote_rejects_last_active_admin_demotion()
    {
        using var db = new TestDb();
        var lone = MkUser("only", admin: true);
        var guest = MkUser("guest", admin: false);
        db.Db.Users.AddRange(lone, guest);
        await db.Db.SaveChangesAsync();

        var d = await AdminUserGuard.CanDemoteAsync(db.Db, actorUserId: 999, lone.Id, currentIsAdmin: true, newIsAdmin: false);
        d.Should().Be(AdminUserGuard.Decision.LastActiveAdmin);
    }

    [Fact]
    public async Task CanDemote_pending_setup_admin_does_not_count_as_active()
    {
        using var db = new TestDb();
        var pending = MkUser("pending-admin", admin: true, pending: true);
        var lone = MkUser("only", admin: true);
        db.Db.Users.AddRange(pending, lone);
        await db.Db.SaveChangesAsync();

        var d = await AdminUserGuard.CanDemoteAsync(db.Db, actorUserId: 999, lone.Id, currentIsAdmin: true, newIsAdmin: false);
        d.Should().Be(AdminUserGuard.Decision.LastActiveAdmin);
    }

    [Fact]
    public async Task CanDemote_allows_promotion_to_admin()
    {
        using var db = new TestDb();
        var u = MkUser("u", admin: false);
        db.Db.Users.Add(u);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDemoteAsync(db.Db, actorUserId: 1, u.Id, currentIsAdmin: false, newIsAdmin: true))
            .Should().Be(AdminUserGuard.Decision.Allow);
    }

    [Fact]
    public async Task CanDemote_allows_no_op_change()
    {
        // IsAdmin が同じ値で来た場合は素通し。
        using var db = new TestDb();
        var u = MkUser("u", admin: true);
        db.Db.Users.Add(u);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDemoteAsync(db.Db, actorUserId: u.Id, u.Id, currentIsAdmin: true, newIsAdmin: true))
            .Should().Be(AdminUserGuard.Decision.Allow);
    }
}
