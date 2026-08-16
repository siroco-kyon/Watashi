using System.Text.Json;
using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.DTOs;

namespace Watashi.Tests;

public class RemotePlaceSettingsTests
{
    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"RemoteFavorites\":\"not-an-array\"}")]
    [InlineData("")]
    public void Corrupt_settings_fall_back_without_exposing_saved_places(string json)
    {
        var settings = AppSettings.DeserializeOrDefault(json);

        settings.RemoteFavorites.Should().BeEmpty();
        settings.RecentRemotePlaces.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_recent_places_are_case_insensitive_and_most_recent_wins()
    {
        var settings = new AppSettings();
        settings.RecordRecentRemotePlace(Place(1, "/Dept/Reports", "old"));

        settings.RecordRecentRemotePlace(Place(1, "\\dept\\reports\\", "new"));

        settings.RecentRemotePlaces.Should().ContainSingle();
        settings.RecentRemotePlaces[0].Path.Should().Be("/dept/reports");
        settings.RecentRemotePlaces[0].DisplayName.Should().Be("new");
    }

    [Fact]
    public void Same_physical_recent_place_replaces_the_permission_evidence()
    {
        var settings = new AppSettings();
        settings.RecordRecentRemotePlace(Place(1, "/dept/reports", "old permission"));

        settings.RecordRecentRemotePlace(Place(2, "/DEPT/REPORTS", "new permission"));

        settings.RecentRemotePlaces.Should().ContainSingle();
        settings.RecentRemotePlaces[0].PermissionId.Should().Be(2);
        settings.RecentRemotePlaces[0].DisplayName.Should().Be("new permission");
    }

    [Fact]
    public void Recent_places_are_newest_first_and_capped()
    {
        var settings = new AppSettings();

        for (var i = 0; i < AppSettings.MaxRecentRemotePlaces + 4; i++)
            settings.RecordRecentRemotePlace(Place(1, $"/root/{i}", $"Place {i}"));

        settings.RecentRemotePlaces.Should().HaveCount(AppSettings.MaxRecentRemotePlaces);
        settings.RecentRemotePlaces[0].Path.Should().Be($"/root/{AppSettings.MaxRecentRemotePlaces + 3}");
        settings.RecentRemotePlaces.Should().NotContain(p => p.Path == "/root/0");
    }

    [Fact]
    public void Favorites_can_be_added_removed_and_are_capped()
    {
        var settings = new AppSettings();
        for (var i = 0; i < AppSettings.MaxRemoteFavorites + 2; i++)
            settings.AddRemoteFavorite(Place(1, $"/favorite/{i}", $"Favorite {i}"));

        settings.RemoteFavorites.Should().HaveCount(AppSettings.MaxRemoteFavorites);
        var selected = settings.RemoteFavorites[3];

        settings.RemoveRemoteFavorite(selected).Should().BeTrue();
        settings.RemoteFavorites.Should().NotContain(p =>
            AppSettings.SameRemotePlace(p, selected));
    }

    [Fact]
    public void Reconcile_keeps_only_the_same_permission_host_share_and_allowed_subtree()
    {
        var settings = new AppSettings
        {
            RemoteFavorites = new List<RemotePlaceSetting>
            {
                Place(10, "/allowed/reports", "valid"),
                Place(11, "/allowed/deleted", "deleted permission"),
                Place(10, "/outside", "outside root"),
                Place(10, "/allowed/../secret", "traversal"),
                Place(10, "/allowed/wrong-host", "wrong host", hostId: 99),
                Place(10, "/allowed/wrong-share", "wrong share", shareId: 99),
            },
            RecentRemotePlaces = new List<RemotePlaceSetting>
            {
                Place(10, "/allowed/recent", "valid recent"),
                Place(20, "/allowed/expired", "expired permission"),
            },
        };
        var current = new[]
        {
            new LocationDto { PermissionId = 10, HostId = 1, ShareId = 2, Path = "/allowed" },
        };

        settings.ReconcileRemotePlaces(current).Should().BeTrue();

        settings.RemoteFavorites.Should().ContainSingle()
            .Which.DisplayName.Should().Be("valid");
        settings.RecentRemotePlaces.Should().ContainSingle()
            .Which.DisplayName.Should().Be("valid recent");
    }

    [Fact]
    public void Serialized_places_restore_across_restart_with_identity_and_label()
    {
        var beforeRestart = new AppSettings();
        beforeRestart.AddRemoteFavorite(Place(7, "/team/docs", "チーム文書", hostId: 3, shareId: 4));
        beforeRestart.RecordRecentRemotePlace(Place(7, "/team/recent", "最近の資料", hostId: 3, shareId: 4));
        var json = JsonSerializer.Serialize(beforeRestart);

        var restored = AppSettings.DeserializeOrDefault(json);

        restored.RemoteFavorites.Should().ContainSingle().Which.Should().BeEquivalentTo(
            Place(7, "/team/docs", "チーム文書", hostId: 3, shareId: 4));
        restored.RecentRemotePlaces.Should().ContainSingle().Which.Should().BeEquivalentTo(
            Place(7, "/team/recent", "最近の資料", hostId: 3, shareId: 4));
    }

    [Fact]
    public void Null_lists_from_json_are_normalized_to_empty_lists()
    {
        var settings = AppSettings.DeserializeOrDefault(
            "{\"RemoteFavorites\":null,\"RecentRemotePlaces\":null}");

        settings.RemoteFavorites.Should().NotBeNull().And.BeEmpty();
        settings.RecentRemotePlaces.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Restart_sanitizes_duplicates_invalid_rows_and_saved_list_overflow()
    {
        var raw = new AppSettings
        {
            RecentRemotePlaces = Enumerable.Range(0, AppSettings.MaxRecentRemotePlaces + 5)
                .Select(i => Place(1, $"/recent/{i}", $"Recent {i}"))
                .Prepend(Place(1, "/recent/0/", "duplicate"))
                .Append(Place(0, "/invalid", "invalid id"))
                .ToList(),
        };

        var restored = AppSettings.DeserializeOrDefault(JsonSerializer.Serialize(raw));

        restored.RecentRemotePlaces.Should().HaveCount(AppSettings.MaxRecentRemotePlaces);
        restored.RecentRemotePlaces.Count(p =>
            AppSettings.SameRemotePlace(p, Place(1, "/recent/0", "ignored"))).Should().Be(1);
        restored.RecentRemotePlaces.Should().NotContain(p => p.PermissionId <= 0);
    }

    private static RemotePlaceSetting Place(
        int permissionId,
        string path,
        string displayName,
        int hostId = 1,
        int shareId = 2)
        => new()
        {
            PermissionId = permissionId,
            HostId = hostId,
            ShareId = shareId,
            Path = path,
            DisplayName = displayName,
        };
}
