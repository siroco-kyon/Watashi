using FluentAssertions;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;
using Xunit;

namespace Watashi.Tests;

public class FileEntryFilterTests
{
    private static FileEntry File(string name) => new() { Name = name, Type = FileEntryTypes.File };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_returns_true_for_empty_term(string? term)
    {
        FileEntryFilter.Matches(File("anything.txt"), term).Should().BeTrue();
    }

    [Fact]
    public void Matches_parent_is_always_true_even_with_term()
    {
        var parent = new FileEntry { Name = "..", Type = FileEntryTypes.Parent };
        FileEntryFilter.Matches(parent, "zzz").Should().BeTrue();
    }

    [Theory]
    [InlineData("report.pdf", "report", true)]
    [InlineData("report.pdf", "PDF", true)]       // 大文字小文字を無視
    [InlineData("report.pdf", "REPORT", true)]
    [InlineData("report.pdf", "ort.p", true)]     // 部分一致
    [InlineData("report.pdf", "xls", false)]
    [InlineData("report.pdf", "  report  ", true)] // 前後空白はトリム
    public void Matches_does_case_insensitive_substring(string name, string term, bool expected)
    {
        FileEntryFilter.Matches(File(name), term).Should().Be(expected);
    }
}
