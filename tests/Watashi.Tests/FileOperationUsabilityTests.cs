using System.Collections.Specialized;
using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Tests;

public class FileOperationUsabilityTests
{
    [Fact]
    public void Two_thousand_entries_publish_one_reset_and_preserve_alias_input()
    {
        var entries = new FileEntryCollection();
        var notifications = new List<NotifyCollectionChangedAction>();
        entries.CollectionChanged += (_, e) => notifications.Add(e.Action);
        entries.ReplaceAll(Enumerable.Range(0, 2000).Select(i => new FileEntry { Name = $"file-{i}" }), "location");
        entries.Should().HaveCount(2000);
        notifications.Should().Equal(NotifyCollectionChangedAction.Reset);
        entries.ReplaceAll(entries.Where(e => e.Name.EndsWith("0")), "location");
        entries.Should().HaveCount(200);
        entries.LocationKey.Should().Be("location");
        notifications.Should().Equal(NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task Batch_continues_after_failure_and_uses_original_selection()
    {
        var selection = new List<string> { "first", "denied", "last" };
        var visited = new List<string>();
        var results = await BatchFileOperation.RunAsync(selection, x => x, item =>
        {
            visited.Add(item);
            selection.Clear();
            return item == "denied" ? Task.FromException(new UnauthorizedAccessException("denied")) : Task.CompletedTask;
        });
        visited.Should().Equal("first", "denied", "last");
        results.Select(x => x.Succeeded).Should().Equal(true, false, true);
        results[1].Error.Should().Be("denied");
    }
}
