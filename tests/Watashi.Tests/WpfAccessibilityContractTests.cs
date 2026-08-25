using System.Xml.Linq;
using FluentAssertions;

namespace Watashi.Tests;

/// <summary>
/// UI Automation の実動作確認は Windows の画面操作テストで行う必要があるが、
/// 重要な XAML 契約が誤って削除されることはプラットフォーム非依存テストでも検知する。
/// </summary>
public class WpfAccessibilityContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("src/Watashi.Client/Views/LoginWindow.xaml")]
    [InlineData("src/Watashi.Client/Views/MainWindow.xaml")]
    [InlineData("src/Watashi.Client/Controls/PasswordRevealBox.xaml")]
    [InlineData("src/Watashi.Client/Themes/Controls.xaml")]
    [InlineData("src/Watashi.Client/Views/ChangePasswordWindow.xaml")]
    [InlineData("src/Watashi.Client/Views/InitialPasswordWindow.xaml")]
    [InlineData("src/Watashi.Client/Views/ConnectionSettingsWindow.xaml")]
    [InlineData("src/Watashi.Client/Views/PersonalSettingsWindow.xaml")]
    [InlineData("src/Watashi.Client/Views/TransferCenterWindow.xaml")]
    [InlineData("src/Watashi.Client/Views/TrustedDevicesWindow.xaml")]
    [InlineData("src/Watashi.Client/Views/Admin/OperationsView.xaml")]
    [InlineData("src/Watashi.Client/Views/PromptDialog.xaml")]
    public void Accessibility_xaml_is_well_formed(string relativePath)
    {
        var act = () => XDocument.Load(RepoFile(relativePath));
        act.Should().NotThrow();
    }

    [Fact]
    public void Login_fields_have_targeted_labels_live_status_and_small_screen_scrolling()
    {
        var doc = Load("src/Watashi.Client/Views/LoginWindow.xaml");

        Named(doc, "UsernameBox").AttributeByLocalName("AutomationProperties.Name")
            .Should().NotBeNullOrWhiteSpace();
        Named(doc, "PasswordBox").AttributeByLocalName("AutomationProperties.Name")
            .Should().NotBeNullOrWhiteSpace();

        doc.Descendants().Where(e => e.Name.LocalName == "Label")
            .Select(e => e.Attribute("Target")?.Value)
            .Should().Contain(v => v != null && v.Contains("UsernameBox", StringComparison.Ordinal))
            .And.Contain(v => v != null && v.Contains("PasswordBox", StringComparison.Ordinal));

        Named(doc, "LoginStatusLiveRegion")
            .AttributeByLocalName("AutomationProperties.LiveSetting").Should().Be("Assertive");
        doc.Descendants().Single(e => e.Name.LocalName == "ScrollViewer")
            .Attribute("VerticalScrollBarVisibility")?.Value.Should().Be("Auto");
    }

    [Fact]
    public void Main_primary_controls_are_named_and_dynamic_messages_are_live_regions()
    {
        var doc = Load("src/Watashi.Client/Views/MainWindow.xaml");
        var automationNames = doc.Descendants()
            .Select(e => e.AttributeByLocalName("AutomationProperties.Name"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        automationNames.Should().Contain(new[]
        {
            "再読み込み",
            "パスワード変更",
            "ログアウト",
            "ローカルパス",
            "ローカル一覧の絞り込み",
            "ローカルファイル一覧",
            "リモート接続場所",
            "リモートパス",
            "リモート一覧の絞り込み",
            "リモートファイル一覧",
        });

        Named(doc, "MainErrorLiveRegion")
            .AttributeByLocalName("AutomationProperties.LiveSetting").Should().Be("Assertive");
        Named(doc, "MainStatusLiveRegion")
            .AttributeByLocalName("AutomationProperties.LiveSetting").Should().Be("Polite");
        doc.Descendants().Count(e => e.Name.LocalName == "WrapPanel").Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Password_reveal_inputs_share_an_accessible_name_and_toggle_is_keyboard_operable()
    {
        var doc = Load("src/Watashi.Client/Controls/PasswordRevealBox.xaml");
        Named(doc, "PartPasswordBox").AttributeByLocalName("AutomationProperties.Name")
            .Should().Contain("AutomationProperties.Name");
        Named(doc, "PartTextBox").AttributeByLocalName("AutomationProperties.Name")
            .Should().Contain("AutomationProperties.Name");

        var toggle = Named(doc, "PartToggle");
        toggle.Attribute("Focusable")?.Value.Should().Be("True");
        toggle.Attribute("IsTabStop")?.Value.Should().Be("True");
        toggle.AttributeByLocalName("AutomationProperties.Name").Should().NotBeNullOrWhiteSpace();

        doc.Descendants().Should().Contain(e =>
            e.Name.LocalName == "Trigger" &&
            e.Attribute("Property") != null &&
            e.Attribute("Property")!.Value == "IsKeyboardFocused");
        File.ReadAllText(RepoFile("src/Watashi.Client/Controls/PasswordRevealBox.xaml"))
            .Should().Contain("SystemParameters.HighContrast");
    }

    [Fact]
    public void Theme_uses_system_colours_for_high_contrast_and_a_visible_focus_adornment()
    {
        var path = RepoFile("src/Watashi.Client/Themes/Controls.xaml");
        var doc = XDocument.Load(path);
        var text = File.ReadAllText(path);

        doc.Descendants().Single(e => e.Attribute(Xaml + "Key")?.Value == "AccessibleFocusVisual")
            .Should().NotBeNull();
        text.Should().Contain("SystemParameters.HighContrast")
            .And.Contain("SystemColors.WindowBrushKey")
            .And.Contain("SystemColors.WindowTextBrushKey")
            .And.Contain("SystemColors.HighlightBrushKey");
    }

    [Fact]
    public void Live_regions_raise_the_required_uia_event()
    {
        File.ReadAllText(RepoFile("src/Watashi.Client/Accessibility/AutomationLiveRegion.cs"))
            .Should().Contain("AutomationEvents.LiveRegionChanged")
            .And.Contain("RaiseAutomationEvent");
    }

    [Fact]
    public void Personal_settings_status_is_named_and_explicitly_announced()
    {
        var doc = Load("src/Watashi.Client/Views/PersonalSettingsWindow.xaml");
        Named(doc, "PersonalSettingsStatusLiveRegion")
            .AttributeByLocalName("AutomationProperties.LiveSetting").Should().Be("Polite");
        File.ReadAllText(RepoFile("src/Watashi.Client/Views/PersonalSettingsWindow.xaml.cs"))
            .Should().Contain("AutomationLiveRegion.Announce(PersonalSettingsStatusLiveRegion)");
    }

    [Theory]
    [InlineData(
        "src/Watashi.Client/Views/TransferCenterWindow.xaml",
        "TransferSummaryLiveRegion",
        "Polite",
        "src/Watashi.Client/Views/TransferCenterWindow.xaml.cs",
        "AutomationLiveRegion.Announce(TransferSummaryLiveRegion)")]
    [InlineData(
        "src/Watashi.Client/Views/TransferCenterWindow.xaml",
        "TransferErrorLiveRegion",
        "Assertive",
        "src/Watashi.Client/Views/TransferCenterWindow.xaml.cs",
        "AutomationLiveRegion.Announce(TransferErrorLiveRegion)")]
    [InlineData(
        "src/Watashi.Client/Views/TrustedDevicesWindow.xaml",
        "TrustedDevicesStatusLiveRegion",
        "Polite",
        "src/Watashi.Client/Views/TrustedDevicesWindow.xaml.cs",
        "AutomationLiveRegion.Announce(TrustedDevicesStatusLiveRegion)")]
    [InlineData(
        "src/Watashi.Client/Views/Admin/OperationsView.xaml",
        "OperationsStatusLiveRegion",
        "Polite",
        "src/Watashi.Client/Views/Admin/OperationsView.xaml.cs",
        "AutomationLiveRegion.Announce(OperationsStatusLiveRegion)")]
    public void Dynamic_status_is_named_and_explicitly_announced(
        string xamlPath, string elementName, string liveSetting,
        string codeBehindPath, string announceCall)
    {
        Named(Load(xamlPath), elementName)
            .AttributeByLocalName("AutomationProperties.LiveSetting").Should().Be(liveSetting);
        File.ReadAllText(RepoFile(codeBehindPath)).Should().Contain(announceCall);
    }

    [Theory]
    [InlineData("src/Watashi.Client/Views/ChangePasswordWindow.xaml", "Current", "NewP", "Confirm")]
    [InlineData("src/Watashi.Client/Views/InitialPasswordWindow.xaml", "NewP", "Confirm", null)]
    public void Password_dialogs_keep_fields_labelled_and_scrollable_at_large_text(
        string relativePath, string firstName, string secondName, string? thirdName)
    {
        var doc = Load(relativePath);
        var targets = doc.Descendants().Where(e => e.Name.LocalName == "Label")
            .Select(e => e.Attribute("Target")?.Value ?? string.Empty)
            .ToList();

        foreach (var name in new[] { firstName, secondName, thirdName }.Where(x => x is not null))
            targets.Should().Contain(v => v.Contains(name!, StringComparison.Ordinal));

        doc.Descendants().Single(e => e.Name.LocalName == "ScrollViewer")
            .Attribute("VerticalScrollBarVisibility")?.Value.Should().Be("Auto");
    }

    private static XDocument Load(string relativePath) => XDocument.Load(RepoFile(relativePath));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => e.Attribute(Xaml + "Name")?.Value == name);

    private static string RepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Watashi.sln")))
            current = current.Parent;
        current.Should().NotBeNull("the test output should be located under the repository");
        return Path.Combine(current!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}

internal static class XamlElementTestExtensions
{
    public static string? AttributeByLocalName(this XElement element, string localName) =>
        element.Attributes().SingleOrDefault(a => a.Name.LocalName == localName)?.Value;
}
