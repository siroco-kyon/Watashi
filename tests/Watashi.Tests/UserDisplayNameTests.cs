using FluentAssertions;
using Watashi.Server.Endpoints;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public sealed class UserDisplayNameTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 山田 太郎 ", "山田 太郎")]
    public void Normalize_trims_and_treats_blank_as_missing(string? input, string? expected)
    {
        UserDisplayNames.TryNormalize(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Fact]
    public void Normalize_enforces_the_published_maximum()
    {
        UserDisplayNames.TryNormalize(new string('名', UserDisplayNames.MaxLength), out var accepted)
            .Should().BeTrue();
        accepted.Should().HaveLength(UserDisplayNames.MaxLength);

        UserDisplayNames.TryNormalize(new string('名', UserDisplayNames.MaxLength + 1), out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("G012345", null, "G012345")]
    [InlineData("G012345", "山田 太郎", "山田 太郎（G012345）")]
    [InlineData("G012345", "  山田 太郎  ", "山田 太郎（G012345）")]
    public void Dtos_use_one_consistent_admin_label(string username, string? displayName, string expected)
    {
        new UserDto { Username = username, DisplayName = displayName }.DisplayLabel.Should().Be(expected);
        new DeviceDto { Username = username, DisplayName = displayName }.DisplayLabel.Should().Be(expected);
        new UserPermissionDto { Username = username, UserDisplayName = displayName }
            .UserDisplayLabel.Should().Be(expected);
        new AuditLogDto { Username = username, DisplayName = displayName }
            .UserDisplayLabel.Should().Be(expected);
    }

    [Fact]
    public void Display_label_uses_name_without_empty_parentheses_when_username_is_missing()
    {
        UserDisplayNames.FormatLabel("山田 太郎", null).Should().Be("山田 太郎");
    }

    [Fact]
    public void Patch_distinguishes_omitted_value_from_explicit_clear()
    {
        AdminUserEndpoints.TryNormalizeDisplayNamePatch(null, out var omitted, out var omittedValue)
            .Should().BeTrue();
        omitted.Should().BeFalse();
        omittedValue.Should().BeNull();

        AdminUserEndpoints.TryNormalizeDisplayNamePatch("   ", out var clear, out var clearValue)
            .Should().BeTrue();
        clear.Should().BeTrue();
        clearValue.Should().BeNull();

        AdminUserEndpoints.TryNormalizeDisplayNamePatch(" 佐藤 花子 ", out var update, out var updateValue)
            .Should().BeTrue();
        update.Should().BeTrue();
        updateValue.Should().Be("佐藤 花子");
    }

    [Fact]
    public void Csv_distinguishes_a_missing_cell_from_an_explicit_empty_cell()
    {
        AdminUserEndpoints.TryReadDisplayNameCell(
                new[] { "G012345", "false" }, 2, out var missing, out var missingValue)
            .Should().BeTrue();
        missing.Should().BeFalse();
        missingValue.Should().BeNull();

        AdminUserEndpoints.TryReadDisplayNameCell(
                new[] { "G012345", "false", "" }, 2, out var empty, out var emptyValue)
            .Should().BeTrue();
        empty.Should().BeTrue();
        emptyValue.Should().BeNull();

        AdminUserEndpoints.TryReadDisplayNameCell(
                new[] { "G012345", "false", " 山田 太郎 " }, 2, out var present, out var presentValue)
            .Should().BeTrue();
        present.Should().BeTrue();
        presentValue.Should().Be("山田 太郎");
    }

    [Fact]
    public void Csv_upsert_preserves_updates_and_clears_an_existing_name_as_documented()
    {
        var user = new User { DisplayName = "変更前" };

        AdminUserEndpoints.ApplyDisplayNameCsvUpdate(user, hasCell: false, normalized: null);
        user.DisplayName.Should().Be("変更前", "旧 CSV や欠落した行末セルは現在値を維持するため");

        AdminUserEndpoints.ApplyDisplayNameCsvUpdate(user, hasCell: true, normalized: "変更後");
        user.DisplayName.Should().Be("変更後");

        AdminUserEndpoints.ApplyDisplayNameCsvUpdate(user, hasCell: true, normalized: null);
        user.DisplayName.Should().BeNull("明示的な空セルは名前を消去するため");
    }

    [Fact]
    public void Csv_escape_neutralizes_a_formula_like_display_name()
    {
        CsvHelper.Escape("=1+1")
            .Should().StartWith("'=");
    }
}
