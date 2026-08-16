using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

public class UserPermissionValidityTests
{
    private sealed record Seed(TestDb Db, User User, CifsShare Share, PermissionTemplate ReadOnly, PermissionTemplate ReadWrite);

    private static async Task<Seed> SeedAsync()
    {
        var db = new TestDb();
        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = "alice",
            PasswordHash = "x",
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(30),
            CreatedAt = now,
        };
        var node = new ExecutionNode
        {
            Name = "Direct",
            NodeType = NodeTypes.Direct,
            HealthStatus = HealthStatuses.Healthy,
            CreatedAt = now,
        };
        var host = new CifsHost
        {
            Name = "host",
            HostAddress = "192.0.2.1",
            CredUsername = "svc",
            CredPasswordEnc = new byte[] { 0 },
            ExecutionNode = node,
            CreatedAt = now,
        };
        var share = new CifsShare { ShareName = "docs", DisplayName = "Docs", Host = host };
        var readOnly = new PermissionTemplate { Name = "RO", CanRead = true };
        var readWrite = new PermissionTemplate { Name = "RW", CanRead = true, CanWrite = true };
        db.Db.AddRange(user, node, host, share, readOnly, readWrite);
        await db.Db.SaveChangesAsync();
        return new Seed(db, user, share, readOnly, readWrite);
    }

    private static UserPermission Permission(
        Seed seed,
        PermissionTemplate template,
        string path,
        DateTime? validFrom = null,
        DateTime? expiresAt = null)
        => new()
        {
            UserId = seed.User.Id,
            ShareId = seed.Share.Id,
            TemplateId = template.Id,
            AllowedPath = path,
            ValidFrom = validFrom,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task Existing_null_window_permission_remains_active()
    {
        var seed = await SeedAsync();
        using var db = seed.Db;
        db.Db.UserPermissions.Add(Permission(seed, seed.ReadOnly, "/dept"));
        await db.Db.SaveChangesAsync();

        var result = await new PermissionService(db.Db).CanPerformAsync(
            seed.User.Id, seed.Share.Id, "/dept/file.txt", Operations.Read);

        result.allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Scheduled_permission_is_excluded_from_authorization_roots_and_locations()
    {
        var seed = await SeedAsync();
        using var db = seed.Db;
        db.Db.UserPermissions.Add(Permission(
            seed, seed.ReadOnly, "/future", validFrom: DateTime.UtcNow.AddHours(1)));
        await db.Db.SaveChangesAsync();
        var service = new PermissionService(db.Db);

        (await service.CanPerformAsync(seed.User.Id, seed.Share.Id, "/future/a", Operations.Read))
            .allowed.Should().BeFalse();
        (await service.IsPermissionRootAsync(seed.User.Id, seed.Share.Id, "/future"))
            .Should().BeFalse();
        (await service.GetUserLocationsAsync(seed.User.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_permission_is_excluded_but_current_window_is_allowed()
    {
        var seed = await SeedAsync();
        using var db = seed.Db;
        db.Db.UserPermissions.AddRange(
            Permission(seed, seed.ReadOnly, "/expired",
                validFrom: DateTime.UtcNow.AddHours(-2), expiresAt: DateTime.UtcNow.AddHours(-1)),
            Permission(seed, seed.ReadOnly, "/current",
                validFrom: DateTime.UtcNow.AddHours(-1), expiresAt: DateTime.UtcNow.AddHours(1)));
        await db.Db.SaveChangesAsync();
        var service = new PermissionService(db.Db);

        (await service.CanPerformAsync(seed.User.Id, seed.Share.Id, "/expired/a", Operations.Read))
            .allowed.Should().BeFalse();
        (await service.CanPerformAsync(seed.User.Id, seed.Share.Id, "/current/a", Operations.Read))
            .allowed.Should().BeTrue();
        (await service.GetUserLocationsAsync(seed.User.Id)).Select(x => x.Path)
            .Should().Equal("/current");
    }

    [Fact]
    public async Task Simulator_uses_the_most_specific_active_grant_and_explains_denials()
    {
        var seed = await SeedAsync();
        using var db = seed.Db;
        var broad = Permission(seed, seed.ReadOnly, "/dept");
        var narrow = Permission(seed, seed.ReadWrite, "/dept/special");
        var expired = Permission(seed, seed.ReadWrite, "/dept/special/deeper",
            expiresAt: DateTime.UtcNow.AddMinutes(-1));
        expired.TemplateId = seed.ReadWrite.Id;
        db.Db.UserPermissions.AddRange(broad, narrow, expired);
        await db.Db.SaveChangesAsync();

        var result = await new PermissionService(db.Db).SimulateAsync(
            seed.User.Id, seed.Share.Id, "/dept/special/deeper/file.txt");

        result.NormalizedPath.Should().Be("/dept/special/deeper/file.txt");
        result.Read.Allowed.Should().BeTrue();
        result.Read.PermissionId.Should().Be(narrow.Id);
        result.Read.MatchRule.Should().Be("active_allowed_path_prefix");
        result.Write.Allowed.Should().BeTrue();
        result.Write.PermissionId.Should().Be(narrow.Id);
        result.Delete.Allowed.Should().BeFalse();
        result.Delete.MatchRule.Should().Be("scope_matched_but_operation_not_granted");
        result.Rename.Allowed.Should().BeFalse();
        result.Read.PermissionId.Should().NotBe(expired.Id);
    }

    [Fact]
    public void Analyzer_reports_broad_duplicate_and_redundant_grants()
    {
        var now = DateTime.UtcNow;
        var grants = new[]
        {
            Grant(1, "/", read: true, write: true),
            Grant(2, "/dept", read: true),
            Grant(3, "/dept", read: true),
        };

        var warnings = UserPermissionRules.Analyze(grants, now);

        warnings[1].Should().Contain(w => w.Code == PermissionWarningCodes.BroadScope);
        warnings[2].Should().Contain(w => w.Code == PermissionWarningCodes.DuplicateScope &&
                                          w.RelatedPermissionIds.Contains(3));
        warnings[3].Should().Contain(w => w.Code == PermissionWarningCodes.DuplicateScope &&
                                          w.RelatedPermissionIds.Contains(2));
        warnings[2].Should().Contain(w => w.Code == PermissionWarningCodes.RedundantGrant &&
                                          w.RelatedPermissionIds.Contains(1));
    }

    [Fact]
    public void Analyzer_does_not_report_duplicate_for_non_overlapping_windows()
    {
        var now = DateTime.UtcNow;
        var first = Grant(1, "/dept", read: true) with { ExpiresAt = now.AddDays(1) };
        var second = Grant(2, "/dept", read: true) with { ValidFrom = now.AddDays(1) };

        var warnings = UserPermissionRules.Analyze(new[] { first, second }, now);

        warnings[1].Should().NotContain(w => w.Code == PermissionWarningCodes.DuplicateScope);
        warnings[2].Should().NotContain(w => w.Code == PermissionWarningCodes.DuplicateScope);
    }

    [Fact]
    public void Metadata_validation_requires_utc_orders_window_trims_and_limits_text()
    {
        var from = DateTime.UtcNow;
        var expires = from.AddHours(1);

        UserPermissionRules.TryNormalizeMetadata(
            from, expires, "  監査対応  ", "  INC-42  ", out var normalized, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
        normalized.Reason.Should().Be("監査対応");
        normalized.TicketNumber.Should().Be("INC-42");

        UserPermissionRules.TryNormalizeMetadata(
            DateTime.SpecifyKind(from, DateTimeKind.Unspecified), expires,
            null, null, out _, out error).Should().BeFalse();
        error.Should().Contain("UTC");

        UserPermissionRules.TryNormalizeMetadata(
            expires, from, null, null, out _, out error).Should().BeFalse();
        error.Should().Contain("後");

        UserPermissionRules.TryNormalizeMetadata(
            null, null, new string('x', CreateUserPermissionRequest.MaxReasonLength + 1),
            null, out _, out error).Should().BeFalse();
        error.Should().Contain(CreateUserPermissionRequest.MaxReasonLength.ToString());
    }

    [Theory]
    [InlineData(null, null, PermissionEffectiveStatuses.Active)]
    public void Null_window_status_is_active(DateTime? from, DateTime? expires, string expected)
    {
        UserPermissionRules.GetEffectiveStatus(from, expires, DateTime.UtcNow).Should().Be(expected);
    }

    private static UserPermissionRules.Grant Grant(
        int id,
        string path,
        bool read = false,
        bool write = false,
        bool delete = false,
        bool rename = false)
        => new(id, 1, 1, 1, "T", path, null, null, read, write, delete, rename);
}
