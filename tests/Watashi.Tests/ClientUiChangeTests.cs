using System.Xml.Linq;
using System.Text.Json;
using FluentAssertions;
using Watashi.Client.Services;

namespace Watashi.Tests;

public sealed class ClientUiChangeTests
{
    [Fact]
    public void Theme_setting_defaults_to_light_and_normalizes_safely()
    {
        AppSettings.DeserializeOrDefault("{}").ThemeMode.Should().Be(AppThemeModes.Light);
        AppSettings.DeserializeOrDefault("""{ "ThemeMode": "dark" }""")
            .ThemeMode.Should().Be(AppThemeModes.Dark);
        AppSettings.DeserializeOrDefault("""{ "ThemeMode": "LIGHT" }""")
            .ThemeMode.Should().Be(AppThemeModes.Light);
        // 旧版の System は ThemeService が起動時に現在の見た目へ一度だけ移行する。
        AppSettings.DeserializeOrDefault("""{ "ThemeMode": "System" }""")
            .ThemeMode.Should().Be(AppThemeModes.System);
        AppSettings.DeserializeOrDefault("""{ "ThemeMode": "unknown" }""")
            .ThemeMode.Should().Be(AppThemeModes.Light);

        var persisted = JsonSerializer.Serialize(new AppSettings { ThemeMode = AppThemeModes.Dark });
        AppSettings.DeserializeOrDefault(persisted).ThemeMode.Should().Be(AppThemeModes.Dark);
    }

    [Fact]
    public void Legacy_system_theme_is_resolved_once_to_a_concrete_mode()
    {
        AppThemeModes.ResolveInitialMode(AppThemeModes.System, isSystemDark: true)
            .Should().Be(AppThemeModes.Dark);
        AppThemeModes.ResolveInitialMode(AppThemeModes.System, isSystemDark: false)
            .Should().Be(AppThemeModes.Light);
        AppThemeModes.ResolveInitialMode(AppThemeModes.Dark, isSystemDark: false)
            .Should().Be(AppThemeModes.Dark);
        AppThemeModes.ResolveInitialMode(AppThemeModes.Light, isSystemDark: true)
            .Should().Be(AppThemeModes.Light);
    }

    [Fact]
    public void Light_and_dark_palettes_expose_the_same_color_keys()
    {
        var light = PaletteKeys("src/Watashi.Client/Themes/Colors.Light.xaml");
        var dark = PaletteKeys("src/Watashi.Client/Themes/Colors.Dark.xaml");

        light.Should().NotBeEmpty();
        dark.Should().BeEquivalentTo(light);

        var semantic = File.ReadAllText(RepoFile("src/Watashi.Client/Themes/Colors.xaml"));
        semantic.Should().Contain("{DynamicResource BgColor}")
            .And.Contain("{DynamicResource TextPrimaryColor}")
            .And.Contain("{DynamicResource AccentColor}");
    }

    [Fact]
    public void Theme_palette_uri_survives_a_branded_assembly_name()
    {
        var service = File.ReadAllText(RepoFile("src/Watashi.Client/Services/ThemeService.cs"));

        service.Should().Contain("typeof(ThemeService).Assembly.GetName().Name")
            .And.NotContain("/Watashi.Client;component/Themes/");
    }

    [Fact]
    public void Main_window_exposes_a_compact_theme_toggle_and_login_has_no_theme_control()
    {
        var main = File.ReadAllText(RepoFile("src/Watashi.Client/Views/MainWindow.xaml"));
        var login = File.ReadAllText(RepoFile("src/Watashi.Client/Views/LoginWindow.xaml"));
        var icons = File.ReadAllText(RepoFile("src/Watashi.Client/Themes/Icons.xaml"));
        var service = File.ReadAllText(RepoFile("src/Watashi.Client/Services/ThemeService.cs"));

        main.Should().Contain("Click=\"OnToggleTheme\"")
            .And.Contain("Theme.ToggleLabel")
            .And.Contain("Theme.IsDarkEffective")
            .And.Contain("MoonIconGeometry")
            .And.Contain("SunIconGeometry")
            .And.Contain("ToolTip=\"{Binding Remote.SelectedLocationTooltip}\"")
            .And.Contain("AutomationProperties.HelpText=\"{Binding Remote.SelectedLocationTooltip}\"");
        login.Should().NotContain("LoginThemeBox")
            .And.NotContain("Theme.SelectedMode")
            .And.NotContain("AutomationProperties.Name=\"表示テーマ\"");
        icons.Should().Contain("x:Key=\"MoonIconGeometry\"")
            .And.Contain("x:Key=\"SunIconGeometry\"");
        service.Should().NotContain("SystemEvents.UserPreferenceChanged")
            .And.NotContain("IReadOnlyList<ThemeOption>")
            .And.NotContain("ThemeMode.System");
    }

    [Fact]
    public void Combo_boxes_pair_windows_background_and_foreground_colors()
    {
        var controls = File.ReadAllText(RepoFile("src/Watashi.Client/Themes/Controls.xaml"));

        controls.Should().Contain("<Style TargetType=\"ComboBoxItem\">")
            .And.Contain("{x:Static wpf:SystemColors.WindowBrushKey}")
            .And.Contain("{x:Static wpf:SystemColors.WindowTextBrushKey}")
            .And.Contain("TextElement.Foreground");
    }

    [Fact]
    public void File_lists_propagate_the_theme_foreground_to_generated_cell_text()
    {
        var controls = File.ReadAllText(RepoFile("src/Watashi.Client/Themes/Controls.xaml"));

        controls.Should().Contain("<Style TargetType=\"ListView\">")
            .And.Contain("<Style TargetType=\"ListViewItem\">")
            .And.Contain("<Setter Property=\"TextElement.Foreground\" Value=\"{StaticResource TextPrimaryBrush}\" />");
    }

    [Fact]
    public void Remote_list_uses_the_server_supported_five_hundred_item_page()
    {
        ApiClient.RemoteListPageSize.Should().Be(500);
        var vm = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/RemotePaneViewModel.cs"));

        Count(vm, "limit: ApiClient.RemoteListPageSize").Should().Be(2);
    }

    [Fact]
    public void Admin_user_surfaces_bind_the_optional_display_name_and_safe_fallback_label()
    {
        var users = File.ReadAllText(RepoFile("src/Watashi.Client/Views/Admin/UserManagementView.xaml"));
        var permissions = File.ReadAllText(RepoFile("src/Watashi.Client/Views/Admin/UserPermissionView.xaml"));
        var devices = File.ReadAllText(RepoFile("src/Watashi.Client/Views/Admin/DeviceManagementView.xaml"));
        var audit = File.ReadAllText(RepoFile("src/Watashi.Client/Views/Admin/AuditLogView.xaml"));
        var userVm = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/Admin/UserManagementViewModel.cs"));
        var permissionVm = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/Admin/UserPermissionViewModel.cs"));
        var deviceVm = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/Admin/DeviceManagementViewModel.cs"));

        users.Should().Contain("Header=\"名前\"")
            .And.Contain("{Binding DisplayName}")
            .And.Contain("{Binding Selected.DisplayLabel")
            .And.Contain("{Binding EditDisplayName")
            .And.Contain("{Binding NewDisplayName");
        permissions.Should().Contain("{Binding DisplayLabel}")
            .And.Contain("DisplayMemberPath=\"DisplayLabel\"");
        devices.Should().Contain("{Binding DisplayLabel}");
        audit.Should().Contain("{Binding UserDisplayLabel}");
        userVm.Should().Contain("u.DisplayName")
            .And.Contain("DisplayName = EditDisplayName")
            .And.Contain("DisplayName = NewDisplayName");
        permissionVm.Should().Contain("u.DisplayName?.Contains")
            .And.Contain("SelectedUser.DisplayLabel");
        deviceVm.Should().Contain("d.DisplayName")
            .And.Contain("SelectedDevice.DisplayLabel");
    }

    [Fact]
    public void Folder_navigation_clears_filters_without_resetting_sort()
    {
        var local = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/LocalPaneViewModel.cs"));
        var remote = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/RemotePaneViewModel.cs"));

        local.Should().Contain("FilterText = string.Empty;");
        remote.Should().Contain("FilterText = string.Empty;");
        local.IndexOf("_all.AddRange(items);", StringComparison.Ordinal).Should().BeLessThan(
            local.IndexOf("if (clearFilterOnSuccess) FilterText = string.Empty;", StringComparison.Ordinal));
        remote.IndexOf("_all.AddRange(res.Entries);", StringComparison.Ordinal).Should().BeLessThan(
            remote.IndexOf("if (clearFilterOnSuccess) FilterText = string.Empty;", StringComparison.Ordinal));
        local.Should().NotContain("SortKey = null;");
        remote.Should().NotContain("SortKey = null;");
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1040, 0, 0, 1920, 1040)]
    [InlineData(-1920, 0, 0, 1080, -1920, 0, 0, 1040, 0, 0, 1920, 1040)]
    [InlineData(0, 0, 1920, 1080, 48, 0, 1920, 1080, 48, 0, 1872, 1080)]
    [InlineData(0, -1200, 1920, 0, 0, -1160, 1920, 0, 0, 40, 1920, 1160)]
    public void Maximize_metrics_are_relative_to_the_target_monitor(
        int ml, int mt, int mr, int mb,
        int wl, int wt, int wr, int wb,
        int x, int y, int width, int height)
    {
        MonitorWorkAreaMath.Calculate(
                new PixelRect(ml, mt, mr, mb),
                new PixelRect(wl, wt, wr, wb))
            .Should().Be(new MaximizedWindowMetrics(x, y, width, height));
    }

    private static HashSet<string> PaletteKeys(string relativePath)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(RepoFile(relativePath)).Root!
            .Elements()
            .Where(e => e.Name.LocalName == "Color")
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static int Count(string text, string value)
        => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string RepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }
}
