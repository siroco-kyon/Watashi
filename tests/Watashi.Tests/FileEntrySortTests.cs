using FluentAssertions;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;
using Xunit;

namespace Watashi.Tests;

public class FileEntrySortTests
{
    private static FileEntry F(string name, long? size = 0, DateTime? modified = null)
        => new() { Name = name, Type = FileEntryTypes.File, Size = size, ModifiedAt = modified };

    private static FileEntry D(string name, DateTime? modified = null)
        => new() { Name = name, Type = FileEntryTypes.Directory, ModifiedAt = modified };

    [Fact]
    public void Sort_default_puts_directories_first_then_name_ascending()
    {
        var entries = new[] { F("banana.txt"), D("zebra"), F("apple.txt"), D("alpha") };
        var sorted = FileEntrySort.Sort(entries, null);
        sorted.Select(e => e.Name).Should().Equal("alpha", "zebra", "apple.txt", "banana.txt");
    }

    [Fact]
    public void Sort_default_name_compare_is_case_insensitive()
    {
        var entries = new[] { F("Banana"), F("apple"), F("Cherry") };
        var sorted = FileEntrySort.Sort(entries, null);
        sorted.Select(e => e.Name).Should().Equal("apple", "Banana", "Cherry");
    }

    [Fact]
    public void Sort_name_desc_reverses_order()
    {
        var entries = new[] { F("a"), F("b"), F("c") };
        var sorted = FileEntrySort.Sort(entries, FileEntrySort.NameDesc);
        sorted.Select(e => e.Name).Should().Equal("c", "b", "a");
    }

    [Fact]
    public void Sort_by_date_ascending_and_descending()
    {
        var t1 = new DateTime(2020, 1, 1);
        var t2 = new DateTime(2021, 1, 1);
        var t3 = new DateTime(2022, 1, 1);
        var entries = new[] { F("b", modified: t2), F("c", modified: t3), F("a", modified: t1) };

        FileEntrySort.Sort(entries, FileEntrySort.Date).Select(e => e.Name).Should().Equal("a", "b", "c");
        FileEntrySort.Sort(entries, FileEntrySort.DateDesc).Select(e => e.Name).Should().Equal("c", "b", "a");
    }

    [Fact]
    public void Sort_by_size_treats_null_as_smallest()
    {
        var entries = new[] { F("big", 300), F("none", null), F("small", 100) };
        FileEntrySort.Sort(entries, FileEntrySort.Size).Select(e => e.Name).Should().Equal("none", "small", "big");
        FileEntrySort.Sort(entries, FileEntrySort.SizeDesc).Select(e => e.Name).Should().Equal("big", "small", "none");
    }

    [Fact]
    public void Sort_excludes_parent_entry()
    {
        var entries = new[]
        {
            new FileEntry { Name = "..", Type = FileEntryTypes.Parent },
            F("file.txt"),
            D("dir"),
        };
        var sorted = FileEntrySort.Sort(entries, null);
        sorted.Should().NotContain(e => e.Type == FileEntryTypes.Parent);
        sorted.Select(e => e.Name).Should().Equal("dir", "file.txt");
    }

    [Theory]
    [InlineData(null, "name", "name")]
    [InlineData("", "name", "name")]
    [InlineData("name", "name", "name_desc")]
    [InlineData("name_desc", "name", "name")]
    [InlineData("date", "name", "name")]   // 別の列へ切り替えるときは昇順から
    [InlineData("name", "size", "size")]
    [InlineData("size", "size", "size_desc")]
    public void Toggle_switches_between_ascending_and_descending(string? current, string column, string expected)
    {
        FileEntrySort.Toggle(current, column).Should().Be(expected);
    }
}
