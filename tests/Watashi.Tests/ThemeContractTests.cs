using System.Globalization;
using System.Xml.Linq;
using FluentAssertions;
using Watashi.Client.Services;

namespace Watashi.Tests;

/// <summary>
/// ダーク/ライト配色の契約。実際の描画確認は Windows 実機での目視が必要だが、
/// 「配色キーの取りこぼし」「ハードコード色の混入」「コントラスト不足」は
/// XAML を読むだけで検知できるので、プラットフォーム非依存テストで守る。
/// </summary>
public class ThemeContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string LightPalette = "src/Watashi.Client/Themes/Colors.Light.xaml";
    private const string DarkPalette = "src/Watashi.Client/Themes/Colors.Dark.xaml";
    private const string BrushLayer = "src/Watashi.Client/Themes/Colors.xaml";
    private const string AppXaml = "src/Watashi.Client/App.xaml";

    [Fact]
    public void Light_and_dark_palettes_define_the_same_color_keys()
    {
        var light = Palette(LightPalette);
        var dark = Palette(DarkPalette);

        dark.Keys.Should().BeEquivalentTo(light.Keys,
            "配色キーが片方だけにあると、そのテーマでブラシの色が解決できず既定色のまま残る");
        light.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(LightPalette)]
    [InlineData(DarkPalette)]
    public void Palette_values_are_opaque_hex_colors(string relativePath)
    {
        foreach (var (key, value) in Palette(relativePath))
            value.Should().MatchRegex("^#[0-9A-Fa-f]{6}$", $"{key} は #RRGGBB 形式で書く");
    }

    /// <summary>
    /// テーマ切替の要。ブラシは色を DynamicResource で引くことで、配色辞書を
    /// 差し替えても同じインスタンスのまま色だけが変わる。StaticResource に
    /// 戻すと読み込み時に値が焼き付き、開いている画面が追従しなくなる。
    /// </summary>
    [Fact]
    public void Brush_layer_resolves_every_color_dynamically()
    {
        var brushes = XDocument.Load(RepoFile(BrushLayer)).Root!.Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToList();

        brushes.Should().NotBeEmpty();
        foreach (var brush in brushes)
        {
            var key = brush.Attribute(Xaml + "Key")?.Value;
            brush.Attribute("Color")?.Value.Should()
                .StartWith("{DynamicResource ", $"{key} は配色辞書の差し替えに追従する必要がある");
        }
    }

    /// <summary>
    /// ブラシ層が配色ファイルを自分でマージすると、そちらが先に検索されて
    /// Application 側の差し替えが効かなくなる。
    /// </summary>
    [Fact]
    public void Brush_layer_does_not_merge_a_palette()
    {
        XDocument.Load(RepoFile(BrushLayer)).Descendants()
            .Any(e => e.Name.LocalName == "MergedDictionaries")
            .Should().BeFalse();
    }

    [Fact]
    public void App_merges_a_palette_before_the_brush_layer()
    {
        var sources = XDocument.Load(RepoFile(AppXaml)).Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary")
            .Select(e => e.Attribute("Source")?.Value)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();

        var palette = sources.FindIndex(s => s.EndsWith("Colors.Light.xaml", StringComparison.Ordinal));
        var brushes = sources.FindIndex(s => s.EndsWith("Colors.xaml", StringComparison.Ordinal));

        palette.Should().BeGreaterThanOrEqualTo(0, "ThemeManager が差し替える配色辞書が必要");
        brushes.Should().BeGreaterThan(palette, "ブラシ層より前に配色が読まれている必要がある");
    }

    /// <summary>
    /// 画面側に色を直接書くと、その箇所だけテーマ切替から取り残される。
    /// 色は必ず Colors.Light.xaml / Colors.Dark.xaml に集約する。
    /// </summary>
    [Fact]
    public void No_xaml_outside_the_palettes_hardcodes_a_color()
    {
        var root = RepoFile("src/Watashi.Client");
        var offenders = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsPaletteFile(path) && !IsBuildOutput(path, root))
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(path), "#[0-9A-Fa-f]{6,8}\\b"))
            .Select(path => Path.GetFileName(path))
            .ToList();

        offenders.Should().BeEmpty("色は配色ファイルに集約する");
    }

    /// <summary>
    /// ダークテーマだけがシステム色を上書きする。WPF 既定テンプレート内部
    /// (ComboBox のドロップダウン、テキスト選択範囲など) を暗くするために必要。
    /// ライト側で同じことをすると、ハイコントラストモードで OS の色を
    /// 参照している Controls.xaml の各 DataTrigger を壊す。
    /// </summary>
    [Fact]
    public void Only_the_dark_palette_overrides_system_colors()
    {
        SystemColorOverrides(DarkPalette).Should().NotBeEmpty();
        SystemColorOverrides(LightPalette).Should().BeEmpty();
    }

    [Fact]
    public void Dark_theme_is_not_applied_in_high_contrast_mode()
    {
        File.ReadAllText(RepoFile("src/Watashi.Client/Services/ThemeManager.cs"))
            .Should().Contain("SystemParameters.HighContrast",
                "ハイコントラストは OS の色が優先されるため、ダーク配色を重ねてはいけない");
    }

    /// <summary>本文・ラベルに使う組み合わせ。WCAG 2.1 AA の通常文字 4.5:1。</summary>
    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Text_colors_meet_wcag_aa(string palette, string foreground, string background)
    {
        Contrast(palette, foreground, background).Should().BeGreaterThanOrEqualTo(4.5);
    }

    /// <summary>枠線やフォーカスリングなど文字以外の視認性。WCAG 2.1 AA の 3:1。</summary>
    [Theory]
    [MemberData(nameof(NonTextPairs))]
    public void Ui_colors_meet_wcag_non_text_contrast(string palette, string foreground, string background)
    {
        Contrast(palette, foreground, background).Should().BeGreaterThanOrEqualTo(3.0);
    }

    public static TheoryData<string, string, string> TextPairs() => Both(new[]
    {
        ("TextPrimaryColor", "BgColor"),
        ("TextPrimaryColor", "SurfaceColor"),
        ("TextPrimaryColor", "SurfaceAltColor"),
        ("TextPrimaryColor", "SurfaceHoverColor"),
        // 選択中の行 (ListViewItem.IsSelected) の文字と背景。
        ("TextPrimaryColor", "AccentSoftColor"),
        ("TextSecondaryColor", "SurfaceColor"),
        ("TextSecondaryColor", "SurfaceAltColor"),
        ("TextSecondaryColor", "BgColor"),
        ("TextMutedColor", "SurfaceColor"),
        ("TextMutedColor", "BgColor"),
        // PrimaryButton の文字。押下・ホバー中も読めること。
        ("TextOnAccentColor", "AccentColor"),
        ("TextOnAccentColor", "AccentHoverColor"),
        ("TextOnAccentColor", "AccentPressedColor"),
        // 警告バナー (AccessibleWarningText / AccessibleRemoteHeaderText)。
        ("AccentDeepColor", "AccentSoftColor"),
        ("DangerColor", "SurfaceColor"),
        ("DangerColor", "BgColor"),
        // エラーバナー (AccessibleErrorBanner + AccessibleErrorText)。
        ("DangerColor", "DangerSoftColor"),
        ("SuccessColor", "SurfaceColor"),
        ("InfoColor", "SurfaceColor"),
    });

    public static TheoryData<string, string, string> NonTextPairs() => Both(new[]
    {
        // 塗りつぶしボタン・タブの下線・プログレスバー。
        ("AccentColor", "SurfaceColor"),
        ("AccentColor", "BgColor"),
        // キーボードフォーカスの枠。
        ("FocusColor", "SurfaceColor"),
        ("FocusColor", "BgColor"),
    });

    private static TheoryData<string, string, string> Both(IEnumerable<(string Fg, string Bg)> pairs)
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (fg, bg) in pairs)
        {
            data.Add(LightPalette, fg, bg);
            data.Add(DarkPalette, fg, bg);
        }
        return data;
    }

    private static double Contrast(string palette, string foreground, string background)
    {
        var colors = Palette(palette);
        colors.Should().ContainKey(foreground).And.ContainKey(background);
        var first = RelativeLuminance(colors[foreground]);
        var second = RelativeLuminance(colors[background]);
        var (lighter, darker) = first >= second ? (first, second) : (second, first);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>WCAG 2.1 の相対輝度。</summary>
    private static double RelativeLuminance(string hex)
    {
        var value = hex.TrimStart('#');
        var r = Channel(value.Substring(0, 2));
        var g = Channel(value.Substring(2, 2));
        var b = Channel(value.Substring(4, 2));
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Channel(string component)
    {
        var value = int.Parse(component, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Dictionary<string, string> Palette(string relativePath) =>
        XDocument.Load(RepoFile(relativePath)).Root!.Elements()
            .Where(e => e.Name.LocalName == "Color" && e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => e.Attribute(Xaml + "Key")!.Value, e => e.Value.Trim());

    private static List<string> SystemColorOverrides(string relativePath) =>
        XDocument.Load(RepoFile(relativePath)).Root!.Elements()
            .Select(e => e.Attribute(Xaml + "Key")?.Value)
            .Where(key => key is not null && key.Contains("SystemColors.", StringComparison.Ordinal))
            .Select(key => key!)
            .ToList();

    private static bool IsPaletteFile(string path)
    {
        var name = Path.GetFileName(path);
        return name is "Colors.Light.xaml" or "Colors.Dark.xaml";
    }

    /// <summary>ビルド生成物はソースではないので対象外にする。</summary>
    private static bool IsBuildOutput(string path, string root) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");

    private static string RepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Watashi.sln")))
            current = current.Parent;
        current.Should().NotBeNull("the test output should be located under the repository");
        return Path.Combine(current!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>settings.json との往復。破損した値で他の設定ごと初期化されないことを確かめる。</summary>
public class ThemeSettingsTests
{
    [Theory]
    [InlineData("system", ThemeMode.System)]
    [InlineData("light", ThemeMode.Light)]
    [InlineData("dark", ThemeMode.Dark)]
    [InlineData("Dark", ThemeMode.Dark)]
    [InlineData("  LIGHT  ", ThemeMode.Light)]
    [InlineData("", ThemeMode.System)]
    [InlineData(null, ThemeMode.System)]
    [InlineData("solarized", ThemeMode.System)]
    public void Parse_falls_back_to_system_for_unknown_values(string? stored, ThemeMode expected)
    {
        ThemeModes.Parse(stored).Should().Be(expected);
    }

    [Theory]
    [InlineData(ThemeMode.System)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void Setting_value_round_trips(ThemeMode mode)
    {
        ThemeModes.Parse(ThemeModes.ToSettingValue(mode)).Should().Be(mode);
    }

    [Fact]
    public void Saved_theme_is_restored()
    {
        var settings = AppSettings.DeserializeOrDefault("""{"Theme":"dark","LastLocalPath":"C:\\work"}""");

        settings.ThemeMode.Should().Be(ThemeMode.Dark);
        settings.LastLocalPath.Should().Be(@"C:\work");
    }

    [Fact]
    public void Unknown_theme_does_not_discard_other_settings()
    {
        var settings = AppSettings.DeserializeOrDefault("""{"Theme":"neon","LastLocalPath":"C:\\work"}""");

        settings.Theme.Should().Be(ThemeModes.SystemValue);
        settings.LastLocalPath.Should().Be(@"C:\work");
    }

    [Fact]
    public void Missing_theme_defaults_to_following_the_os()
    {
        AppSettings.DeserializeOrDefault("""{"LastLocalPath":"C:\\work"}""")
            .ThemeMode.Should().Be(ThemeMode.System);
    }
}
