using FluentAssertions;
using Watashi.Server.Services;
using Xunit;

namespace Watashi.Tests;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("Aa1!")]
    [InlineData("Aa1!aaaaaaa")]    // 11 文字
    public void Rejects_passwords_under_minimum_length(string pw)
    {
        var (ok, err) = PasswordPolicy.Validate(pw);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("alllowercase!1234567")]  // upper missing
    [InlineData("ALLUPPERCASE!1234567")]  // lower missing
    [InlineData("Mixedcase!aaaaaaaaaa")]  // digit missing
    [InlineData("Mixedcase1aaaaaaaaaa")]  // symbol missing
    public void Rejects_passwords_missing_character_class(string pw)
    {
        var (ok, err) = PasswordPolicy.Validate(pw);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Admin123!@#X")]              // ちょうど 12 文字、4 種
    [InlineData("StrongPass2026!")]           // 一般的
    [InlineData("こんにちはAa1!XXa")]         // 日本語 + 4 種 (記号扱い) ちょうど 12 文字
    [InlineData("Pa$$w0rdPa$$w0rd")]          // 長め
    public void Accepts_passwords_meeting_policy(string pw)
    {
        var (ok, err) = PasswordPolicy.Validate(pw);
        ok.Should().BeTrue();
        err.Should().BeNull();
    }

    [Fact]
    public void Minimum_length_constant_is_exposed()
    {
        // ドキュメント/UI と整合性が取れているかの自己テスト
        PasswordPolicy.MinLength.Should().BeGreaterThanOrEqualTo(12);
    }
}
