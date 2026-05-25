using FluentAssertions;
using Watashi.Shared.Helpers;
using Xunit;

namespace Watashi.Tests;

public class CsvHelperTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("alice", "alice")]
    public void Escape_passes_through_safe_values(string? input, string expected)
    {
        CsvHelper.Escape(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("=cmd|'/c calc'!A1", "'=cmd|'/c calc'!A1")]   // 数式インジェクション
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+5", "'-2+5")]
    [InlineData("@SUM", "'@SUM")]
    [InlineData("\tlead-tab", "'\tlead-tab")]
    // \r は危険な先頭文字でもあり、IndexOfAny の CRLF 検出にも引っかかるため、
    // 先頭にシングルクォートを付けた上でさらに二重クォートで囲む。
    [InlineData("\rlead-cr", "\"'\rlead-cr\"")]
    public void Escape_prefixes_dangerous_lead_characters_with_apostrophe(string input, string expected)
    {
        CsvHelper.Escape(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("a,b,c", "\"a,b,c\"")]                  // カンマを含むセル
    [InlineData("line1\nline2", "\"line1\nline2\"")]   // 改行
    [InlineData("he said \"hi\"", "\"he said \"\"hi\"\"\"")]  // ダブルクォート
    public void Escape_quotes_when_special_characters_present(string input, string expected)
    {
        CsvHelper.Escape(input).Should().Be(expected);
    }

    [Fact]
    public void Escape_handles_dangerous_lead_with_special_chars_together()
    {
        // =, カンマ両方を含むケース: 先頭にシングルクォート + 全体をダブルクォートで囲む
        CsvHelper.Escape("=A1,B1").Should().Be("\"'=A1,B1\"");
    }

    [Theory]
    [InlineData('=', true)]
    [InlineData('+', true)]
    [InlineData('-', true)]
    [InlineData('@', true)]
    [InlineData('\t', true)]
    [InlineData('\r', true)]
    [InlineData('a', false)]
    [InlineData('1', false)]
    [InlineData(' ', false)]
    public void IsDangerousLeadChar_classifies_correctly(char c, bool expected)
    {
        CsvHelper.IsDangerousLeadChar(c).Should().Be(expected);
    }
}
