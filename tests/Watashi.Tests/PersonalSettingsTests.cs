using System.Text.Json;
using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.DTOs;
using Watashi.Shared.Helpers;

namespace Watashi.Tests;

public class PersonalSettingsTests
{
    [Fact]
    public void Existing_settings_keep_the_previous_startup_behavior()
    {
        var settings = AppSettings.DeserializeOrDefault("{}");

        settings.LocalStartupMode.Should().Be(LocalStartupModes.LastUsed);
        settings.RemoteStartupMode.Should().Be(RemoteStartupModes.None);
        settings.RememberSortOrder.Should().BeFalse();
        settings.UseRecycleBinForLocalDeletes.Should().BeTrue();
    }

    [Fact]
    public void Invalid_modes_and_sort_keys_are_normalized_safely()
    {
        var settings = AppSettings.DeserializeOrDefault("""
            {
              "LocalStartupMode": "unknown",
              "RemoteStartupMode": "unknown",
              "RememberSortOrder": true,
              "LocalSortKey": "invalid",
              "RemoteSortKey": "name_desc"
            }
            """);

        settings.LocalStartupMode.Should().Be(LocalStartupModes.LastUsed);
        settings.RemoteStartupMode.Should().Be(RemoteStartupModes.None);
        settings.LocalSortKey.Should().BeNull();
        settings.RemoteSortKey.Should().Be(FileEntrySort.NameDesc);
    }

    [Fact]
    public void Fixed_local_folder_precedes_last_used_and_profile_without_duplicates()
    {
        var settings = new AppSettings
        {
            LocalStartupMode = LocalStartupModes.Fixed,
            FixedLocalStartupPath = @"D:\Work",
            LastLocalPath = @"C:\Last",
        };

        LocalStartupPathCandidates.Build(settings, @"C:\Users\alice")
            .Should().Equal(@"D:\Work", @"C:\Last", @"C:\Users\alice");

        settings.LastLocalPath = @"d:\work";
        LocalStartupPathCandidates.Build(settings, @"C:\Users\alice")
            .Should().Equal(@"D:\Work", @"C:\Users\alice");
    }

    [Fact]
    public void Personal_preferences_round_trip_through_json()
    {
        var place = Place(7, "/team/docs", "チーム文書", 3, 4);
        var before = new AppSettings
        {
            LocalStartupMode = LocalStartupModes.Fixed,
            FixedLocalStartupPath = @"D:\Downloads",
            RemoteStartupMode = RemoteStartupModes.Favorite,
            RemoteStartupPlace = place,
            RemoteFavorites = new List<RemotePlaceSetting> { place },
            LastRemotePlace = Place(7, "/team/recent", "最近", 3, 4),
            ThemeMode = AppThemeModes.Dark,
            UseRecycleBinForLocalDeletes = false,
            RememberSortOrder = true,
            LocalSortKey = FileEntrySort.DateDesc,
            RemoteSortKey = FileEntrySort.Size,
        };

        var restored = AppSettings.DeserializeOrDefault(JsonSerializer.Serialize(before));

        restored.LocalStartupMode.Should().Be(LocalStartupModes.Fixed);
        restored.FixedLocalStartupPath.Should().Be(@"D:\Downloads");
        restored.RemoteStartupMode.Should().Be(RemoteStartupModes.Favorite);
        restored.RemoteStartupPlace.Should().BeEquivalentTo(place);
        restored.ThemeMode.Should().Be(AppThemeModes.Dark);
        restored.UseRecycleBinForLocalDeletes.Should().BeFalse();
        restored.RememberSortOrder.Should().BeTrue();
        restored.LocalSortKey.Should().Be(FileEntrySort.DateDesc);
        restored.RemoteSortKey.Should().Be(FileEntrySort.Size);
    }

    [Fact]
    public void Reconcile_disables_startup_places_that_are_no_longer_authorized()
    {
        var allowed = Place(10, "/allowed/favorite", "有効");
        var denied = Place(20, "/denied/last", "無効");
        var settings = new AppSettings
        {
            RemoteStartupMode = RemoteStartupModes.LastUsed,
            RemoteStartupPlace = allowed,
            LastRemotePlace = denied,
        };

        settings.ReconcileRemotePlaces(new[]
        {
            new LocationDto { PermissionId = 10, HostId = 1, ShareId = 2, Path = "/allowed" },
        }).Should().BeTrue();

        settings.RemoteStartupPlace.Should().BeEquivalentTo(allowed);
        settings.LastRemotePlace.Should().BeNull();
        settings.RemoteStartupMode.Should().Be(RemoteStartupModes.None);
    }

    [Fact]
    public void Clearing_history_keeps_favorites_and_disables_last_used_startup()
    {
        var favorite = Place(10, "/favorite", "お気に入り");
        var settings = new AppSettings
        {
            RemoteFavorites = new List<RemotePlaceSetting> { favorite },
            RecentRemotePlaces = new List<RemotePlaceSetting> { Place(10, "/recent", "最近") },
            LastRemotePlace = Place(10, "/last", "前回"),
            RemoteStartupMode = RemoteStartupModes.LastUsed,
        };

        settings.ClearRemoteHistory().Should().BeTrue();

        settings.RemoteFavorites.Should().ContainSingle().Which.Should().BeEquivalentTo(favorite);
        settings.RecentRemotePlaces.Should().BeEmpty();
        settings.LastRemotePlace.Should().BeNull();
        settings.RemoteStartupMode.Should().Be(RemoteStartupModes.None);
    }

    [Fact]
    public void Removing_the_configured_favorite_disables_fixed_remote_startup()
    {
        var favorite = Place(10, "/favorite", "お気に入り");
        var settings = new AppSettings
        {
            RemoteFavorites = new List<RemotePlaceSetting> { favorite },
            RemoteStartupMode = RemoteStartupModes.Favorite,
            RemoteStartupPlace = favorite,
        };

        settings.RemoveRemoteFavorite(favorite).Should().BeTrue();

        settings.RemoteStartupPlace.Should().BeNull();
        settings.RemoteStartupMode.Should().Be(RemoteStartupModes.None);
    }

    [Fact]
    public void Reset_keeps_favorites_and_history_but_restores_preferences()
    {
        var settings = new AppSettings
        {
            LocalStartupMode = LocalStartupModes.Fixed,
            FixedLocalStartupPath = @"D:\Work",
            RemoteStartupMode = RemoteStartupModes.Favorite,
            RemoteStartupPlace = Place(10, "/favorite", "お気に入り"),
            RemoteFavorites = new List<RemotePlaceSetting> { Place(10, "/favorite", "お気に入り") },
            RecentRemotePlaces = new List<RemotePlaceSetting> { Place(10, "/recent", "最近") },
            ThemeMode = AppThemeModes.Dark,
            UseRecycleBinForLocalDeletes = false,
            RememberSortOrder = true,
            LocalSortKey = FileEntrySort.NameDesc,
        };

        settings.ResetPersonalPreferences();

        settings.LocalStartupMode.Should().Be(LocalStartupModes.LastUsed);
        settings.RemoteStartupMode.Should().Be(RemoteStartupModes.None);
        settings.ThemeMode.Should().Be(AppThemeModes.Light);
        settings.UseRecycleBinForLocalDeletes.Should().BeTrue();
        settings.RememberSortOrder.Should().BeFalse();
        settings.RemoteFavorites.Should().ContainSingle();
        settings.RecentRemotePlaces.Should().ContainSingle();
    }

    [Fact]
    public void Personal_settings_window_exposes_all_agreed_controls()
    {
        var xaml = File.ReadAllText(RepoFile("src/Watashi.Client/Views/PersonalSettingsWindow.xaml"));
        var main = File.ReadAllText(RepoFile("src/Watashi.Client/Views/MainWindow.xaml"));

        main.Should().Contain("Click=\"OnOpenPersonalSettings\"")
            .And.Contain("AutomationProperties.Name=\"個人設定\"");
        xaml.Should().Contain("前回開いていたフォルダ")
            .And.Contain("指定したお気に入り")
            .And.Contain("一覧の並び順を次回も使用する")
            .And.Contain("Windowsのごみ箱を使う")
            .And.Contain("最近使ったリモート場所を消去")
            .And.Contain("個人設定を初期値に戻す");
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

    private static string RepoFile(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath));
}
