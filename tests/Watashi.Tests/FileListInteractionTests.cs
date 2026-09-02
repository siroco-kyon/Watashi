using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Tests;

public sealed class FileListInteractionTests
{
    [Theory]
    [InlineData("folder.csv", FileEntryTypes.Directory, FileEntryVisualCategories.Directory)]
    [InlineData("REPORT.CSV", FileEntryTypes.File, FileEntryVisualCategories.Csv)]
    [InlineData("notes.txt", FileEntryTypes.File, FileEntryVisualCategories.Text)]
    [InlineData("appsettings.JSON", FileEntryTypes.File, FileEntryVisualCategories.Text)]
    [InlineData("report.xlsx", FileEntryTypes.File, FileEntryVisualCategories.Document)]
    [InlineData("photo.JPEG", FileEntryTypes.File, FileEntryVisualCategories.Image)]
    [InlineData("backup.tar.gz", FileEntryTypes.File, FileEntryVisualCategories.Archive)]
    [InlineData("README", FileEntryTypes.File, FileEntryVisualCategories.Other)]
    [InlineData("..", FileEntryTypes.Parent, FileEntryVisualCategories.Other)]
    public void File_entries_are_classified_case_insensitively(string name, string type, string expected)
    {
        FileEntryVisualCategories.Classify(new FileEntry { Name = name, Type = type })
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("sample.dat", FileColorPaletteKeys.Teal)]
    [InlineData("NOTES.TXT", FileColorPaletteKeys.Gray)]
    [InlineData("installer.EXE", FileColorPaletteKeys.Red)]
    [InlineData("table.csv", FileColorPaletteKeys.Green)]
    public void Default_personal_rules_include_requested_extensions(string name, string expectedColor)
    {
        FileColorRules.ResolveColorKey(Entry(name), FileColorRules.CreateDefaults())
            .Should().Be(expectedColor);
    }

    [Fact]
    public void User_rules_are_case_insensitive_and_prefer_the_longest_suffix()
    {
        var rules = new[]
        {
            new FileColorRule { Name = "gzip", ColorKey = FileColorPaletteKeys.Brown, Extensions = [".gz"] },
            new FileColorRule { Name = "tar gzip", ColorKey = FileColorPaletteKeys.Teal, Extensions = [".tar.gz"] },
        };

        FileColorRules.ResolveColorKey(Entry("BACKUP.TAR.GZ"), rules)
            .Should().Be(FileColorPaletteKeys.Teal);
    }

    [Theory]
    [InlineData("txt", ".txt")]
    [InlineData("*.DAT", ".dat")]
    [InlineData(" .tar.gz ", ".tar.gz")]
    public void Extension_input_is_normalized(string input, string expected)
    {
        FileColorRules.TryNormalizeExtension(input, out var actual).Should().BeTrue();
        actual.Should().Be(expected);
    }

    [Fact]
    public void Dragging_a_selected_row_preserves_the_whole_selection_and_excludes_parent()
    {
        var first = Entry("first.txt");
        var second = Entry("second.csv");
        var parent = new FileEntry { Name = "..", Type = FileEntryTypes.Parent };

        FileDragSelection.Build(second, clickedEntryIsSelected: true, new[] { parent, first, second, first })
            .Should().Equal(first, second);
    }

    [Fact]
    public void Dragging_an_unselected_row_uses_only_that_row()
    {
        var selected = Entry("selected.txt");
        var clicked = Entry("clicked.txt");

        FileDragSelection.Build(clicked, clickedEntryIsSelected: false, new[] { selected })
            .Should().Equal(clicked);
    }

    [Fact]
    public void File_type_colours_are_an_opt_in_persisted_personal_setting()
    {
        AppSettings.DeserializeOrDefault("{}").EnableFileTypeColors.Should().BeFalse();

        var restored = AppSettings.DeserializeOrDefault(
            JsonSerializer.Serialize(new AppSettings { EnableFileTypeColors = true }));
        restored.EnableFileTypeColors.Should().BeTrue();

        var applied = new AppSettings();
        applied.ApplyPersistentState(restored);
        applied.EnableFileTypeColors.Should().BeTrue();
        applied.ResetPersonalPreferences();
        applied.EnableFileTypeColors.Should().BeFalse();
        applied.FileColorRules.Should().Contain(x => x.Extensions.Contains(".dat"));
    }

    [Fact]
    public void Custom_file_colour_rules_are_persisted_deep_copied_and_normalized()
    {
        var original = new AppSettings
        {
            FileColorRules =
            [
                new FileColorRule
                {
                    Name = "My data",
                    Extensions = ["DAT", "*.Bin"],
                    ColorKey = FileColorPaletteKeys.Teal,
                },
            ],
        };

        var restored = AppSettings.DeserializeOrDefault(JsonSerializer.Serialize(original));
        restored.FileColorRules.Should().ContainSingle();
        restored.FileColorRules[0].Extensions.Should().Equal(".dat", ".bin");

        var applied = new AppSettings();
        applied.ApplyPersistentState(restored);
        restored.FileColorRules[0].Extensions[0] = ".changed";
        applied.FileColorRules[0].Extensions.Should().Equal(".dat", ".bin");
    }

    [Fact]
    public void Missing_or_null_colour_rules_migrate_to_defaults()
    {
        AppSettings.DeserializeOrDefault("{}").FileColorRules
            .Should().Contain(x => x.Extensions.Contains(".exe"));
        AppSettings.DeserializeOrDefault("{\"FileColorRules\":null}").FileColorRules
            .Should().Contain(x => x.Extensions.Contains(".txt"));
    }

    [Fact]
    public void Personal_setting_is_wired_to_save_and_refresh_both_file_lists()
    {
        var settingsWindow = File.ReadAllText(RepoFile("src/Watashi.Client/Views/PersonalSettingsWindow.xaml"));
        var settingsViewModel = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/PersonalSettingsViewModel.cs"));
        var mainViewModel = File.ReadAllText(RepoFile("src/Watashi.Client/ViewModels/MainViewModel.cs"));
        var mainWindow = File.ReadAllText(RepoFile("src/Watashi.Client/Views/MainWindow.xaml.cs"));

        settingsWindow.Should().Contain("IsChecked=\"{Binding EnableFileTypeColors, Mode=TwoWay}\"");
        settingsViewModel.Should().Contain("candidate.EnableFileTypeColors = EnableFileTypeColors;")
            .And.Contain("EnableFileTypeColors = _settings.EnableFileTypeColors;")
            .And.Contain("candidate.FileColorRules = fileColorRules;");
        mainViewModel.Should().Contain("OnPropertyChanged(nameof(EnableFileTypeColors));")
            .And.Contain("OnPropertyChanged(nameof(FileColorRules));")
            .And.Contain("Local.ApplyUserPreferences();")
            .And.Contain("Remote.ApplyUserPreferences();");
        mainWindow.Should().Contain("if (saved) _vm.ApplyUserPreferences();");
    }

    [Fact]
    public void File_lists_use_a_dedicated_scroll_lane_and_row_only_drag_start()
    {
        var main = File.ReadAllText(RepoFile("src/Watashi.Client/Views/MainWindow.xaml"));
        var controls = File.ReadAllText(RepoFile("src/Watashi.Client/Themes/Controls.xaml"));
        var dragDrop = File.ReadAllText(RepoFile("src/Watashi.Client/Views/MainWindow.DragDrop.cs"));

        Count(main, "ScrollViewer.VerticalScrollBarVisibility=\"Visible\"").Should().Be(2);
        Count(main, "BasedOn=\"{StaticResource FileListScrollBarStyle}\"").Should().Be(2);
        controls.Should().Contain("x:Key=\"FileListScrollBarStyle\"")
            .And.Contain("Property=\"Width\" Value=\"18\"")
            .And.Contain("x:Key=\"ScrollBarPageButtonStyle\"")
            .And.Contain("Property=\"OverridesDefaultStyle\" Value=\"True\"")
            .And.Contain("Property=\"HorizontalAlignment\" Value=\"Stretch\"")
            .And.Contain("Property=\"VerticalAlignment\" Value=\"Stretch\"")
            .And.Contain("Style=\"{StaticResource ScrollBarPageButtonStyle}\"")
            .And.Contain("ScrollBar.PageDownCommand")
            .And.Contain("ScrollBar.PageUpCommand")
            .And.Contain("Orientation=\"{TemplateBinding Orientation}\"")
            .And.Contain("CommandTarget=\"{Binding RelativeSource={RelativeSource TemplatedParent}}\"");
        dragDrop.Should().Contain("FindAncestor<ListViewItem>")
            .And.Contain("FileDragSelection.Build")
            .And.Contain("_deferSelectionCollapse");
        main.Should().Contain("SelectionTextBrush")
            .And.Contain("SystemParameters.HighContrast")
            .And.Contain("Binding Foreground");
    }

    [Fact]
    public void Double_click_opening_is_limited_to_file_rows()
    {
        var mainWindow = File.ReadAllText(RepoFile("src/Watashi.Client/Views/MainWindow.xaml.cs"));

        mainWindow.Should().Contain("IsFileListRowInput(sender, e.OriginalSource)")
            .And.Contain("ItemsControl.ContainerFromElement(list, source) is ListViewItem");
        Count(mainWindow, "if (!IsFileListRowInput(sender, e.OriginalSource)) return;")
            .Should().Be(3, "ローカル、リモート、横断検索の全一覧で行外ダブルクリックを無視するため");
    }

    [Fact]
    public void Focus_visual_has_no_blue_dotted_outline()
    {
        var controls = File.ReadAllText(RepoFile("src/Watashi.Client/Themes/Controls.xaml"));

        controls.Should().Contain("x:Key=\"AccessibleFocusVisual\"")
            .And.Contain("{StaticResource FocusBrush}")
            .And.NotContain("StrokeDashArray");
    }

    [Fact]
    public void Every_file_type_colour_meets_text_contrast_in_both_themes()
    {
        var keys = new[]
        {
            "FileDirectoryColor", "FileCsvColor", "FileTextColor",
            "FileDocumentColor", "FileImageColor", "FileArchiveColor",
            "FileBlueColor", "FileGreenColor", "FileGrayColor", "FilePurpleColor",
            "FileOrangeColor", "FileBrownColor", "FileTealColor", "FileRedColor",
        };

        foreach (var path in new[]
                 {
                     "src/Watashi.Client/Themes/Colors.Light.xaml",
                     "src/Watashi.Client/Themes/Colors.Dark.xaml",
                 })
        {
            var colors = Palette(path);
            foreach (var key in keys)
                Contrast(colors[key], colors["SurfaceColor"])
                    .Should().BeGreaterThanOrEqualTo(4.5, $"{path} の {key}");
        }
    }

    private static FileEntry Entry(string name) => new() { Name = name, Type = FileEntryTypes.File };

    private static Dictionary<string, string> Palette(string relativePath)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(RepoFile(relativePath)).Root!
            .Elements()
            .Where(element => element.Name.LocalName == "Color")
            .ToDictionary(element => element.Attribute(x + "Key")!.Value, element => element.Value, StringComparer.Ordinal);
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var rgb = Enumerable.Range(0, 3)
                .Select(i => Convert.ToInt32(hex.Substring(1 + (i * 2), 2), 16) / 255d)
                .Select(value => value <= 0.04045
                    ? value / 12.92
                    : Math.Pow((value + 0.055) / 1.055, 2.4))
                .ToArray();
            return (0.2126 * rgb[0]) + (0.7152 * rgb[1]) + (0.0722 * rgb[2]);
        }

        var values = new[] { Luminance(first), Luminance(second) };
        return (values.Max() + 0.05) / (values.Min() + 0.05);
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
