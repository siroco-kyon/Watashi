using FluentAssertions;
using Watashi.Server.Services;

namespace Watashi.Tests;

public sealed class KeyedAsyncLockTests
{
    [Fact]
    public async Task Same_key_is_serialized_and_unused_entries_are_removed()
    {
        var keyed = new KeyedAsyncLock<string>();
        var inside = 0;
        var maxInside = 0;

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var lease = await keyed.AcquireAsync("same", CancellationToken.None);
            var current = Interlocked.Increment(ref inside);
            maxInside = Math.Max(maxInside, current);
            await Task.Yield();
            Interlocked.Decrement(ref inside);
        });
        await Task.WhenAll(tasks);

        maxInside.Should().Be(1);
        keyed.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_poison_key()
    {
        var keyed = new KeyedAsyncLock<int>();
        using var holder = await keyed.AcquireAsync(1, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = keyed.AcquireAsync(1, cts.Token).AsTask();
        cts.Cancel();
        await FluentActions.Awaiting(async () => await waiting)
            .Should().ThrowAsync<OperationCanceledException>();
        holder.Dispose();

        using (await keyed.AcquireAsync(1, CancellationToken.None)) { }
        keyed.EntryCount.Should().Be(0);
    }
}
