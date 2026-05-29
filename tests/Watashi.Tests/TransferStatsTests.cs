using FluentAssertions;
using Watashi.Shared.Helpers;
using Xunit;

namespace Watashi.Tests;

public class TransferStatsTests
{
    [Fact]
    public void BytesPerSecond_returns_zero_for_nonpositive_elapsed()
    {
        TransferStats.BytesPerSecond(1000, TimeSpan.Zero).Should().Be(0);
        TransferStats.BytesPerSecond(1000, TimeSpan.FromSeconds(-1)).Should().Be(0);
    }

    [Fact]
    public void BytesPerSecond_divides_bytes_by_seconds()
    {
        TransferStats.BytesPerSecond(1000, TimeSpan.FromSeconds(2)).Should().Be(500);
    }

    [Fact]
    public void Eta_is_null_when_not_computable()
    {
        TransferStats.Eta(0, 1000, 0).Should().BeNull();        // 速度 0
        TransferStats.Eta(0, 0, 100).Should().BeNull();         // 総量 0
        TransferStats.Eta(1000, 1000, 100).Should().BeNull();   // すでに完了
        TransferStats.Eta(1500, 1000, 100).Should().BeNull();   // 完了量が総量超過
    }

    [Fact]
    public void Eta_computes_remaining_time()
    {
        // 残り 500 バイトを 100 B/s で → 5 秒
        TransferStats.Eta(500, 1000, 100).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0, "-")]
    [InlineData(-5, "-")]
    [InlineData(512, "512.0 B/s")]
    [InlineData(1024, "1.0 KB/s")]
    [InlineData(1536, "1.5 KB/s")]
    [InlineData(1048576, "1.0 MB/s")]
    public void FormatSpeed_picks_appropriate_unit(double bytesPerSecond, string expected)
    {
        TransferStats.FormatSpeed(bytesPerSecond).Should().Be(expected);
    }

    [Fact]
    public void FormatEta_is_dash_when_null()
    {
        TransferStats.FormatEta(null).Should().Be("-");
    }

    [Theory]
    [InlineData(0, 0, 45, "00:45")]
    [InlineData(0, 5, 9, "05:09")]
    [InlineData(1, 2, 3, "1:02:03")]
    [InlineData(2, 0, 0, "2:00:00")]
    public void FormatEta_uses_mm_ss_or_h_mm_ss(int hours, int minutes, int seconds, string expected)
    {
        var eta = new TimeSpan(hours, minutes, seconds);
        TransferStats.FormatEta(eta).Should().Be(expected);
    }
}
