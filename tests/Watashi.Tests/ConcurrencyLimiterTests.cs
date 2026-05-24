using FluentAssertions;
using Watashi.Agent.Services;
using Xunit;

namespace Watashi.Tests;

public class ConcurrencyLimiterTests
{
    [Fact]
    public void TryEnter_respects_initial_max()
    {
        var limiter = new ConcurrencyLimiter(2);
        var a = limiter.TryEnter();
        var b = limiter.TryEnter();
        var c = limiter.TryEnter();
        a.Should().NotBeNull();
        b.Should().NotBeNull();
        c.Should().BeNull();
        a!.Dispose(); b!.Dispose();
    }

    [Fact]
    public void SetMax_can_raise_limit_for_subsequent_acquires()
    {
        var limiter = new ConcurrencyLimiter(1);
        var first = limiter.TryEnter();
        first.Should().NotBeNull();
        limiter.TryEnter().Should().BeNull(); // 上限に達している

        limiter.SetMax(3);
        limiter.Max.Should().Be(3);
        var second = limiter.TryEnter();
        var third = limiter.TryEnter();
        second.Should().NotBeNull();
        third.Should().NotBeNull();

        first!.Dispose(); second!.Dispose(); third!.Dispose();
    }

    [Fact]
    public void SetMax_clamps_to_minimum_one()
    {
        var limiter = new ConcurrencyLimiter(5);
        limiter.SetMax(0);
        limiter.Max.Should().Be(1);
        limiter.SetMax(-10);
        limiter.Max.Should().Be(1);
    }
}
