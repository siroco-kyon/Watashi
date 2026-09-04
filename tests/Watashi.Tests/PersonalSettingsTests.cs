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
    public void Extension_sort_keys_survive_a_save_and_reload()
    {
        var restored = AppSettings.DeserializeOrDefault(JsonSerializer.Serialize(new AppSettings
        {
            RememberSortOrder = true,
            LocalSortKey = FileEntrySort.Ext,
            RemoteSortKey = FileEntrySort.ExtDesc,
        }));

        restored.LocalSortKey.Should().Be(FileEntrySort.Ext);
        restored.RemoteSortKey.Should().Be(FileEntrySort.ExtDesc);
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
    public void Readding_the_startup_favorite_through_a_new_permission_updates_the_startup_record()
    {
        var oldFavorite = Place(10, "/favorite", "旧権限");
        var replacement = Place(20, "/favorite", "新権限");
        var settings = new AppSettings
        {
            RemoteFavorites = new List<RemotePlaceSetting> { oldFavorite },
            RemoteStartupMode = RemoteStartupModes.Favorite,
            RemoteStartupPlace = oldFavorite,
        };

        settings.AddRemoteFavorite(replacement).Should().BeTrue();

        settings.RemoteFavorites.Should().ContainSingle().Which.PermissionId.Should().Be(20);
        settings.RemoteStartupPlace.Should().NotBeNull();
        settings.RemoteStartupPlace!.PermissionId.Should().Be(20);
        settings.RemoteStartupMode.Should().Be(RemoteStartupModes.Favorite);
    }

    [Fact]
    public void Persistent_copy_does_not_mutate_live_settings_until_applied()
    {
        var source = new AppSettings
        {
            ServerUrl = "https://fixed.example",
            UseRecycleBinForLocalDeletes = true,
            RecentRemotePlaces = new List<RemotePlaceSetting> { Place(10, "/recent", "最近") },
            LastRemotePlace = Place(10, "/last", "前回"),
        };
        var candidate = source.CreatePersistentCopy();

        candidate.UseRecycleBinForLocalDeletes = false;
        candidate.ClearRemoteHistory();

        source.UseRecycleBinForLocalDeletes.Should().BeTrue();
        source.RecentRemotePlaces.Should().ContainSingle();
        source.LastRemotePlace.Should().NotBeNull();

        source.ApplyPersistentState(candidate);

        source.UseRecycleBinForLocalDeletes.Should().BeFalse();
        source.RecentRemotePlaces.Should().BeEmpty();
        source.LastRemotePlace.Should().BeNull();
        source.ServerUrl.Should().Be("https://fixed.example");
    }

    [Fact]
    public async Task Directory_availability_checks_existing_and_missing_paths_off_the_caller()
    {
        var existing = Path.Combine(Path.GetTempPath(), "watashi-directory-probe-" + Guid.NewGuid());
        Directory.CreateDirectory(existing);
        try
        {
            // 既定の 5 秒はCIのスレッドプールが詰まると Task.Run の開始が間に合わず
            // TimedOut になり得る。ここで見たいのは存在判定なので、余裕のある値を明示する。
            var timeout = TimeSpan.FromSeconds(60);
            (await DirectoryAvailability.ProbeAsync(existing, timeout: timeout))
                .Should().Be(DirectoryAvailabilityResult.Exists);
            (await DirectoryAvailability.ProbeAsync(Path.Combine(existing, "missing"), timeout: timeout))
                .Should().Be(DirectoryAvailabilityResult.Missing);
        }
        finally
        {
            Directory.Delete(existing);
        }
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

    [Fact]
    public void Startup_and_settings_safety_guards_remain_wired()
    {
        var local = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/LocalPaneViewModel.cs"));
        var remote = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/RemotePaneViewModel.cs"));
        var settings = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/PersonalSettingsViewModel.cs"));
        var main = File.ReadAllText(RepoFile("src/Watashi.Client/Views/MainWindow.xaml.cs"));

        local.Should().Contain("generation != Volatile.Read(ref _refreshGeneration)")
            .And.Contain("!string.Equals(path, CurrentPath");
        remote.Should().Contain("recordLast: true")
            .And.Contain("startupGeneration != Volatile.Read(ref _refreshGeneration)");
        settings.Should().Contain("CreatePersistentCopy()")
            .And.Contain("ApplyPersistentState(candidate)")
            .And.Contain("DirectoryAvailability.ProbeAsync");
        main.Should().Contain("Task.WhenAll(localInitialization, transferInitialization, remoteInitialization)");
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
