using FluentAssertions;
using Watashi.Client.Services;

namespace Watashi.Tests;

public class AppSettingsTransportTests
{
    [Fact]
    public void Local_delete_uses_windows_recycle_bin_by_default_and_round_trips_opt_out()
    {
        AppSettings.DeserializeOrDefault("{}").UseRecycleBinForLocalDeletes.Should().BeTrue();

        var restored = AppSettings.DeserializeOrDefault("""
            { "UseRecycleBinForLocalDeletes": false }
            """);

        restored.UseRecycleBinForLocalDeletes.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://watashi.test", true, false)]
    [InlineData("HTTP://WATASHI.TEST:18080", true, false)]
    [InlineData("https://watashi.test", false, true)]
    [InlineData("httpx://watashi.test", false, false)]
    [InlineData("not-a-url", false, false)]
    public void Transport_is_derived_from_the_absolute_uri_scheme(
        string serverUrl, bool expectedHttp, bool expectedHttps)
    {
        var settings = new AppSettings { ServerUrl = serverUrl };

        settings.IsHttp.Should().Be(expectedHttp);
        settings.IsHttps.Should().Be(expectedHttps);
    }

    [Fact]
    public void Http_diagnostic_explicitly_warns_that_traffic_is_unencrypted()
    {
        var settings = new AppSettings { ServerUrl = "http://watashi.test" };

        settings.TransportSecurityDiagnostic.Should().StartWith("⚠ HTTP:");
        settings.TransportSecurityDiagnostic.Should().Contain("暗号化されません");
    }

    [Fact]
    public void Https_diagnostic_reports_tls()
    {
        var settings = new AppSettings { ServerUrl = "https://watashi.test" };

        settings.TransportSecurityDiagnostic.Should().StartWith("✓ HTTPS:");
        settings.TransportSecurityDiagnostic.Should().Contain("TLS");
    }
}
