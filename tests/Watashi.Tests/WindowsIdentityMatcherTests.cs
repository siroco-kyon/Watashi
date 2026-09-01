using FluentAssertions;
using Watashi.Server.Auth;
using Xunit;

namespace Watashi.Tests;

/// <summary>
/// OS アカウント名の正規化と照合。副作用が無い純粋な判定なので、
/// ドメイン参加環境が無くても NetBIOS / UPN / 名前のみの全形式を検証できる。
/// </summary>
public class WindowsIdentityMatcherTests
{
    [Fact]
    public void Defaults_ignore_the_domain_for_IIS_compatibility()
    {
        var options = new WindowsAuthOptions { Mode = WindowsAuthModes.Negotiate };

        options.DomainMatch.Should().Be(WindowsAuthDomainMatchModes.IgnoreDomain);
        WindowsIdentityMatcher.Matches(@"CORP\G012345", "G012345", options).Should().BeTrue();
        Action validate = options.Validate;
        validate.Should().NotThrow();
    }

    private static WindowsAuthOptions IgnoreDomain() => new()
    {
        Mode = WindowsAuthModes.Negotiate,
        DomainMatch = WindowsAuthDomainMatchModes.IgnoreDomain,
    };

    private static WindowsAuthOptions AllowList(params string[] domains) => new()
    {
        Mode = WindowsAuthModes.Negotiate,
        DomainMatch = WindowsAuthDomainMatchModes.AllowList,
        AllowedDomains = domains.ToList(),
    };

    // ===== Split =====

    [Theory]
    [InlineData(@"CORP\G012345", "CORP", "G012345")]
    [InlineData(@"corp.example.com\G012345", "corp.example.com", "G012345")]
    [InlineData("G012345@corp.example.com", "corp.example.com", "G012345")]
    [InlineData("G012345", null, "G012345")]
    [InlineData("  G012345  ", null, "G012345")]
    public void Split_handles_every_account_name_form(string raw, string? expectedDomain, string expectedAccount)
    {
        var (domain, account) = WindowsIdentityMatcher.Split(raw);
        domain.Should().Be(expectedDomain);
        account.Should().Be(expectedAccount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_returns_empty_account_for_missing_input(string? raw)
    {
        var (domain, account) = WindowsIdentityMatcher.Split(raw);
        domain.Should().BeNull();
        account.Should().BeEmpty();
    }

    // ===== Matches =====

    [Theory]
    [InlineData(@"CORP\G012345")]
    [InlineData("G012345@corp.example.com")]
    [InlineData("G012345")]
    public void Matches_accepts_the_same_account_in_any_form(string raw)
    {
        WindowsIdentityMatcher.Matches(raw, "G012345", IgnoreDomain()).Should().BeTrue();
    }

    [Fact]
    public void Matches_ignores_case()
    {
        WindowsIdentityMatcher.Matches(@"corp\g012345", "G012345", IgnoreDomain()).Should().BeTrue();
    }

    [Fact]
    public void Matches_rejects_a_different_account()
    {
        WindowsIdentityMatcher.Matches(@"CORP\G999999", "G012345", IgnoreDomain()).Should().BeFalse();
    }

    [Fact]
    public void Matches_rejects_an_account_name_that_only_shares_a_prefix()
    {
        WindowsIdentityMatcher.Matches(@"CORP\G012345X", "G012345", IgnoreDomain()).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"CORP\")]
    public void Matches_rejects_a_missing_account_name(string? raw)
    {
        WindowsIdentityMatcher.Matches(raw, "G012345", IgnoreDomain()).Should().BeFalse();
    }

    [Fact]
    public void Matches_rejects_a_missing_watashi_username()
    {
        WindowsIdentityMatcher.Matches(@"CORP\G012345", null, IgnoreDomain()).Should().BeFalse();
        WindowsIdentityMatcher.Matches(@"CORP\G012345", "  ", IgnoreDomain()).Should().BeFalse();
    }

    // ===== ドメイン照合モード =====

    [Fact]
    public void IgnoreDomain_accepts_any_domain()
    {
        var opts = IgnoreDomain();
        WindowsIdentityMatcher.Matches(@"CORP\G012345", "G012345", opts).Should().BeTrue();
        WindowsIdentityMatcher.Matches(@"OTHER\G012345", "G012345", opts).Should().BeTrue();
        WindowsIdentityMatcher.Matches("G012345", "G012345", opts).Should().BeTrue();
    }

    [Fact]
    public void AllowList_accepts_only_listed_domains()
    {
        var opts = AllowList("CORP", "corp.example.com");
        WindowsIdentityMatcher.Matches(@"CORP\G012345", "G012345", opts).Should().BeTrue();
        WindowsIdentityMatcher.Matches("G012345@corp.example.com", "G012345", opts).Should().BeTrue();
        // 同名ユーザーが別ドメインに居ても乗っ取れない。
        WindowsIdentityMatcher.Matches(@"OTHER\G012345", "G012345", opts).Should().BeFalse();
    }

    [Fact]
    public void AllowList_ignores_case_and_padding_in_the_configured_domains()
    {
        var opts = AllowList("  corp  ");
        WindowsIdentityMatcher.Matches(@"CORP\G012345", "G012345", opts).Should().BeTrue();
    }

    [Fact]
    public void AllowList_rejects_names_without_a_domain_part()
    {
        // ワークグループのローカルアカウントなど。AllowList を選んだ以上は通さない。
        WindowsIdentityMatcher.Matches("G012345", "G012345", AllowList("CORP")).Should().BeFalse();
    }

    [Fact]
    public void AllowList_with_an_empty_list_rejects_everything()
    {
        // 設定漏れで全ドメイン素通し、という事故を起こさない。
        WindowsIdentityMatcher.Matches(@"CORP\G012345", "G012345", AllowList()).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("AllowLis")]
    [InlineData("unknown")]
    public void Unknown_domain_match_mode_is_fail_closed(string mode)
    {
        var opts = IgnoreDomain();
        opts.DomainMatch = mode;

        WindowsIdentityMatcher.Matches(@"CORP\G012345", "G012345", opts).Should().BeFalse();
        Action validate = opts.Validate;
        validate.Should().Throw<InvalidOperationException>();
    }
}
