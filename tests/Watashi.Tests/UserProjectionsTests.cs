using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Services;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

/// <summary>
/// 管理画面へ返す PasswordStatus の判定。
///
/// この射影は SQL に変換されて実行される。DateTime 列は値コンバータで ISO-8601 の TEXT として
/// 保存されているため、期限比較が文字列比較になっても正しく動くかを本物の SQLite で確かめる。
/// </summary>
public class UserProjectionsTests
{
    private static User Mk(string name, bool pending = false, bool mustChange = false, int expiresInDays = 30)
    {
        var now = DateTime.UtcNow;
        return new User
        {
            Username = name,
            PasswordHash = pending ? PasswordSetup.CreateUnusableHash() : "hash",
            IsPasswordSetupPending = pending,
            MustChangePassword = mustChange,
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(expiresInDays),
            CreatedAt = now,
        };
    }

    private static async Task<Dictionary<string, UserDto>> ProjectAsync(TestDb db)
    {
        // 本番と同じ式をそのまま使う (SQL 変換されることの確認も兼ねる)。
        var items = await db.Db.Users.AsNoTracking()
            .Select(UserProjections.ToDto(DateTime.UtcNow))
            .ToListAsync();
        return items.ToDictionary(x => x.Username);
    }

    [Fact]
    public async Task Password_status_covers_every_state()
    {
        using var db = new TestDb();
        db.Db.Users.AddRange(
            Mk("normal"),
            Mk("pending", pending: true),
            Mk("must-change", mustChange: true),
            Mk("expired", expiresInDays: -1));
        await db.Db.SaveChangesAsync();

        var byName = await ProjectAsync(db);

        byName["normal"].PasswordStatus.Should().Be(PasswordStatuses.Active);
        byName["pending"].PasswordStatus.Should().Be(PasswordStatuses.PendingSetup);
        byName["must-change"].PasswordStatus.Should().Be(PasswordStatuses.MustChange);
        // TEXT 列 (ISO-8601) 同士の比較が時系列として機能していることの確認。
        byName["expired"].PasswordStatus.Should().Be(PasswordStatuses.Expired);
    }

    [Fact]
    public async Task Pending_setup_wins_over_expired_and_must_change()
    {
        // 未設定はまだログインできないので、期限や要変更より先に判定されなければならない。
        using var db = new TestDb();
        db.Db.Users.Add(Mk("pending", pending: true, mustChange: true, expiresInDays: -1));
        await db.Db.SaveChangesAsync();

        (await ProjectAsync(db))["pending"].PasswordStatus.Should().Be(PasswordStatuses.PendingSetup);
    }

    [Fact]
    public async Task Expired_wins_over_must_change()
    {
        using var db = new TestDb();
        db.Db.Users.Add(Mk("u", mustChange: true, expiresInDays: -1));
        await db.Db.SaveChangesAsync();

        (await ProjectAsync(db))["u"].PasswordStatus.Should().Be(PasswordStatuses.Expired);
    }

    [Fact]
    public async Task Projection_never_exposes_the_password_hash()
    {
        using var db = new TestDb();
        db.Db.Users.Add(Mk("u"));
        await db.Db.SaveChangesAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(await ProjectAsync(db));

        json.Should().NotContain("hash");
        json.Should().NotContain("PasswordHash");
    }

    [Fact]
    public async Task Setup_fields_are_carried_through()
    {
        using var db = new TestDb();
        var due = DateTime.UtcNow.AddDays(7);
        var u = Mk("u", pending: true);
        u.PasswordSetupExpiresAt = due;
        u.WindowsAccountName = @"CORP\G012345";
        db.Db.Users.Add(u);
        await db.Db.SaveChangesAsync();

        var dto = (await ProjectAsync(db))["u"];

        dto.PasswordSetupExpiresAt.Should().BeCloseTo(due, TimeSpan.FromSeconds(1));
        dto.WindowsAccountName.Should().Be(@"CORP\G012345");
    }

    [Fact]
    public async Task Disable_evidence_is_carried_through()
    {
        using var db = new TestDb();
        var at = DateTime.UtcNow.AddHours(-1);
        var u = Mk("u");
        u.IsDisabled = true;
        u.DisabledAt = at;
        u.DisabledReason = "契約終了";
        u.DisabledByUserId = 42;
        u.DisabledByUsername = "admin";
        db.Db.Users.Add(u);
        await db.Db.SaveChangesAsync();

        var dto = (await ProjectAsync(db))["u"];

        dto.IsDisabled.Should().BeTrue();
        dto.DisabledAt.Should().BeCloseTo(at, TimeSpan.FromSeconds(1));
        dto.DisabledReason.Should().Be("契約終了");
        dto.DisabledByUserId.Should().Be(42);
        dto.DisabledByUsername.Should().Be("admin");
    }

    [Theory]
    [InlineData(PasswordStatuses.PendingSetup, "初回設定待ち")]
    [InlineData(PasswordStatuses.Expired, "期限切れ")]
    [InlineData(PasswordStatuses.MustChange, "要変更")]
    [InlineData(PasswordStatuses.Active, "有効")]
    public void Status_labels_are_translated_for_the_admin_ui(string status, string expected)
    {
        new UserDto { PasswordStatus = status }.PasswordStatusLabel.Should().Be(expected);
    }

    [Theory]
    [InlineData(false, false, "有効")]
    [InlineData(true, false, "ロック")]
    [InlineData(false, true, "無効")]
    [InlineData(true, true, "無効")]
    public void Account_status_prioritizes_explicit_disable(bool locked, bool disabled, string expected)
    {
        new UserDto { IsLocked = locked, IsDisabled = disabled }
            .AccountStatusLabel.Should().Be(expected);
    }
}
