using FluentAssertions;
using Watashi.Shared.Helpers;
using Xunit;

namespace Watashi.Tests;

public class PathHelperTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/a", "/a")]
    [InlineData("/a/", "/a")]
    [InlineData("a/b", "/a/b")]
    [InlineData("/a\\b", "/a/b")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/b/..", "/a")]
    [InlineData("/a/b/../../c", "/c")]
    [InlineData("/../..", "/")]
    [InlineData("/a//b///c", "/a/b/c")]
    public void Normalize_should_canonicalize(string? input, string expected)
    {
        PathHelper.NormalizePath(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("/", "/anything/inside", true)]
    [InlineData("/", "/", true)]
    [InlineData("/dept-A", "/dept-A", true)]
    [InlineData("/dept-A", "/dept-A/sub", true)]
    [InlineData("/dept-A", "/dept-B", false)]
    [InlineData("/dept-A", "/dept-AB", false)]
    [InlineData("/dept-A", "/", false)]
    [InlineData("/Dept-A", "/dept-a/file.txt", true)] // case-insensitive
    public void IsPathWithin_should_check_subpath(string allowed, string requested, bool expected)
    {
        PathHelper.IsPathWithin(allowed, requested).Should().Be(expected);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a", "/")]
    [InlineData("/a/b", "/a")]
    [InlineData("/a/b/c", "/a/b")]
    public void GetParent_should_return_parent(string input, string expected)
    {
        PathHelper.GetParent(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("/a/../../etc/passwd", "/etc/passwd")] // traversal collapses
    [InlineData("/a/b/c/../../..", "/")]
    public void Normalize_should_collapse_traversal(string input, string expected)
    {
        PathHelper.NormalizePath(input).Should().Be(expected);
    }
}
