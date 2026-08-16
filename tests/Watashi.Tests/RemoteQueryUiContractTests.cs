using System.Xml.Linq;
using FluentAssertions;

namespace Watashi.Tests;

public sealed class RemoteQueryUiContractTests
{
    [Fact]
    public void Remote_pane_keeps_saved_places_and_exposes_bounded_incremental_search_controls()
    {
        var path = RepoFile("src/Watashi.Client/Views/MainWindow.xaml");
        var text = File.ReadAllText(path);
        var doc = XDocument.Load(path);

        text.Should().Contain("Remote.AddCurrentFavoriteCommand")
            .And.Contain("Remote.LoadMoreCommand")
            .And.Contain("Remote.SearchRemoteCommand")
            .And.Contain("Remote.CancelSearchCommand")
            .And.Contain("Remote.LoadMoreSearchCommand")
            .And.Contain("非常に大きい単一フォルダーは列挙完了後に停止します")
            .And.Contain("VirtualizingPanel.VirtualizationMode=\"Recycling\"");

        var automationNames = doc.Descendants()
            .Select(e => e.Attributes().FirstOrDefault(a =>
                a.Name.LocalName == "AutomationProperties.Name")?.Value)
            .Where(x => x is not null)
            .ToList();
        automationNames.Should().Contain("権限内リモート横断検索")
            .And.Contain("リモート一覧をさらに読み込む")
            .And.Contain("リモート横断検索結果");
    }

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
