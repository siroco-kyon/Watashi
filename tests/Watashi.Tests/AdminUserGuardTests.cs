using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

public class AdminUserGuardTests
{
    [Fact]
    public async Task Mutation_lease_serializes_last_admin_check_and_update_sequences()
    {
        await using var first = await AdminUserGuard.AcquireMutationLeaseAsync();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            await using var lease = await AdminUserGuard.AcquireMutationLeaseAsync();
            secondEntered.SetResult();
        });

        await Task.Delay(50);
        secondEntered.Task.IsCompleted.Should().BeFalse();
        await first.DisposeAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        secondEntered.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static User MkUser(
        string name, bool admin, bool locked = false, bool pending = false, bool disabled = false) => new()
        {
            Username = name,
            PasswordHash = "x",
            IsAdmin = admin,
            IsLocked = locked,
            IsDisabled = disabled,
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
    public async Task CanDelete_disabled_admin_does_not_count_as_active()
    {
        using var db = new TestDb();
        var disabled = MkUser("disabled-admin", admin: true, disabled: true);
        var lone = MkUser("only", admin: true);
        db.Db.Users.AddRange(disabled, lone);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDeleteAsync(db.Db, actorUserId: 999, lone.Id, lone.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.LastActiveAdmin);
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

    // ===== CanRequireSetupAsync =====

    [Fact]
    public async Task CanRequireSetup_rejects_self_target()
    {
        // 自分を初回設定待ちに戻すと、その場でログイン手段を失う。
        using var db = new TestDb();
        var alice = MkUser("alice", admin: true);
        var bob = MkUser("bob", admin: true);
        db.Db.Users.AddRange(alice, bob);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanRequireSetupAsync(db.Db, alice.Id, alice.Id, alice.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.SelfTarget);
    }

    [Fact]
    public async Task CanRequireSetup_rejects_the_last_active_admin()
    {
        using var db = new TestDb();
        var lone = MkUser("only", admin: true);
        var guest = MkUser("guest", admin: false);
        db.Db.Users.AddRange(lone, guest);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanRequireSetupAsync(db.Db, actorUserId: 999, lone.Id, lone.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.LastActiveAdmin);
    }

    [Fact]
    public async Task CanRequireSetup_allows_a_non_admin()
    {
        using var db = new TestDb();
        var admin = MkUser("admin", admin: true);
        var guest = MkUser("guest", admin: false);
        db.Db.Users.AddRange(admin, guest);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanRequireSetupAsync(db.Db, admin.Id, guest.Id, guest.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.Allow);
    }

    [Fact]
    public async Task CanRequireSetup_allows_an_admin_when_another_active_admin_exists()
    {
        using var db = new TestDb();
        var a = MkUser("a", admin: true);
        var b = MkUser("b", admin: true);
        db.Db.Users.AddRange(a, b);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanRequireSetupAsync(db.Db, a.Id, b.Id, b.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.Allow);
    }

    // ===== CanDisableAsync =====

    [Fact]
    public async Task CanDisable_rejects_self_target()
    {
        using var db = new TestDb();
        var alice = MkUser("alice", admin: true);
        var bob = MkUser("bob", admin: true);
        db.Db.Users.AddRange(alice, bob);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDisableAsync(db.Db, alice.Id, alice.Id, alice.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.SelfTarget);
    }

    [Fact]
    public async Task CanDisable_rejects_last_active_admin()
    {
        using var db = new TestDb();
        var lone = MkUser("only", admin: true);
        var disabled = MkUser("disabled", admin: true, disabled: true);
        db.Db.Users.AddRange(lone, disabled);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDisableAsync(db.Db, actorUserId: 999, lone.Id, lone.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.LastActiveAdmin);
    }

    [Fact]
    public async Task CanDisable_allows_admin_when_another_active_admin_exists()
    {
        using var db = new TestDb();
        var a = MkUser("a", admin: true);
        var b = MkUser("b", admin: true);
        db.Db.Users.AddRange(a, b);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDisableAsync(db.Db, a.Id, b.Id, b.IsAdmin))
            .Should().Be(AdminUserGuard.Decision.Allow);
    }

    [Fact]
    public async Task CanDisable_allows_non_admin()
    {
        using var db = new TestDb();
        var admin = MkUser("admin", admin: true);
        var guest = MkUser("guest", admin: false);
        db.Db.Users.AddRange(admin, guest);
        await db.Db.SaveChangesAsync();

        (await AdminUserGuard.CanDisableAsync(db.Db, admin.Id, guest.Id, guest.IsAdmin))
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
