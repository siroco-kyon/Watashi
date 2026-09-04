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

    [Fact]
    public void Sort_by_extension_ascending_and_descending()
    {
        var entries = new[] { F("b.xlsx"), F("a.pdf"), F("c.txt") };

        FileEntrySort.Sort(entries, FileEntrySort.Ext).Select(e => e.Name)
            .Should().Equal("a.pdf", "c.txt", "b.xlsx");
        FileEntrySort.Sort(entries, FileEntrySort.ExtDesc).Select(e => e.Name)
            .Should().Equal("b.xlsx", "c.txt", "a.pdf");
    }

    [Fact]
    public void Sort_by_extension_falls_back_to_name_ascending_within_the_same_extension()
    {
        var entries = new[] { F("zebra.txt"), F("Apple.TXT"), F("mango.txt") };

        // 降順でも二次キーの名前は昇順のまま (エクスプローラーと同じ)。
        FileEntrySort.Sort(entries, FileEntrySort.Ext).Select(e => e.Name)
            .Should().Equal("Apple.TXT", "mango.txt", "zebra.txt");
        FileEntrySort.Sort(entries, FileEntrySort.ExtDesc).Select(e => e.Name)
            .Should().Equal("Apple.TXT", "mango.txt", "zebra.txt");
    }

    [Fact]
    public void Sort_by_extension_groups_folders_and_extensionless_files_as_empty()
    {
        var entries = new[] { F("report.pdf"), D("photos"), F("README"), D("archive") };

        FileEntrySort.Sort(entries, FileEntrySort.Ext).Select(e => e.Name)
            .Should().Equal("archive", "photos", "README", "report.pdf");
        FileEntrySort.Sort(entries, FileEntrySort.ExtDesc).Select(e => e.Name)
            .Should().Equal("report.pdf", "archive", "photos", "README");
    }

    [Theory]
    [InlineData("report.XLSX", "xlsx")]
    [InlineData("manual.pdf", "pdf")]
    [InlineData("backup.TAR.GZ", "tar.gz")]
    [InlineData("logs.tar.bz2", "tar.bz2")]
    [InlineData("report.v1.2.xlsx", "xlsx")]
    [InlineData("README", "")]
    [InlineData(".gitignore", "")]
    [InlineData("trailing.", "")]
    public void GetExtension_lowercases_and_keeps_known_compound_extensions(string name, string expected)
    {
        FileEntrySort.GetExtension(F(name)).Should().Be(expected);
    }

    [Fact]
    public void GetExtension_is_empty_for_folders_parents_and_null()
    {
        FileEntrySort.GetExtension(D("photos.bak")).Should().BeEmpty();
        FileEntrySort.GetExtension(new FileEntry { Name = "..", Type = FileEntryTypes.Parent }).Should().BeEmpty();
        FileEntrySort.GetExtension(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "name", "name")]
    [InlineData("", "name", "name")]
    [InlineData("name", "name", "name_desc")]
    [InlineData("name_desc", "name", "name")]
    [InlineData("date", "name", "name")]   // 別の列へ切り替えるときは昇順から
    [InlineData("name", "size", "size")]
    [InlineData("size", "size", "size_desc")]
    [InlineData(null, "ext", "ext")]
    [InlineData("ext", "ext", "ext_desc")]
    [InlineData("ext_desc", "ext", "ext")]
    [InlineData("ext", "name", "name")]
    public void Toggle_switches_between_ascending_and_descending(string? current, string column, string expected)
    {
        FileEntrySort.Toggle(current, column).Should().Be(expected);
    }
}
