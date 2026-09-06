using FluentAssertions;
using Watashi.Client.Services;

namespace Watashi.Tests;

public partial class TransferQueueServiceTests
{
    [Fact]
    public async Task Completed_cleanup_preserves_interrupted_and_only_discards_confirmed_ids()
    {
        using var temp = new TemporaryDirectory();
        var jobs = new[] { TransferJobStates.Completed, TransferJobStates.Skipped, TransferJobStates.Failed, TransferJobStates.Canceled }
            .Select(state => HistoryJob(state, state, DateTime.UtcNow)).ToArray();
        var store = new TransferQueueStore(Path.Combine(temp.Path, "queue.json"));
        await store.SaveAsync(jobs);
        await using var queue = new TransferQueueService(store, new FakeProtocol());
        await queue.InitializeAsync();
        await queue.RemoveFinishedAsync();
        (await queue.SnapshotAsync()).Select(j => j.State).Should().BeEquivalentTo(TransferJobStates.Failed, TransferJobStates.Canceled);
        await queue.DiscardInterruptedAsync([jobs[2].Id]);
        (await queue.SnapshotAsync()).Should().ContainSingle().Which.Id.Should().Be(jobs[3].Id);
    }

    [Fact]
    public async Task Discard_save_failure_preserves_partial_data_and_history()
    {
        using var temp = new TemporaryDirectory();
        var job = HistoryJob("failed", TransferJobStates.Failed, DateTime.UtcNow);
        job.Direction = TransferDirections.Download;
        job.LocalPath = Path.Combine(temp.Path, "download.bin");
        var partial = Path.Combine(temp.Path, $".download.bin.{job.Id}.watashi-part");
        await File.WriteAllBytesAsync(partial, [1, 2, 3]);
        var store = new ControllableStore(Path.Combine(temp.Path, "queue.json"));
        await store.SaveAsync([job]);
        await using var queue = new TransferQueueService(store, new FakeProtocol());
        await queue.InitializeAsync();
        store.FailAllSaves = true;
        await Assert.ThrowsAsync<IOException>(() => queue.DiscardInterruptedAsync([job.Id]));
        File.Exists(partial).Should().BeTrue();
        (await queue.SnapshotAsync()).Should().ContainSingle();
        store.FailAllSaves = false;
        await queue.DiscardInterruptedAsync([job.Id]);
        File.Exists(partial).Should().BeFalse();
    }

    [Fact]
    public async Task Batch_cancel_only_affects_confirmed_jobs()
    {
        using var temp = new TemporaryDirectory();
        var store = new TransferQueueStore(Path.Combine(temp.Path, "queue.json"));
        var jobs = new[] { HistoryJob("first", TransferJobStates.Paused, DateTime.UtcNow), HistoryJob("new", TransferJobStates.Paused, DateTime.UtcNow) };
        await store.SaveAsync(jobs);
        await using var queue = new TransferQueueService(store, new FakeProtocol());
        await queue.InitializeAsync();
        await queue.CancelJobsAsync([jobs[0].Id]);
        var snapshot = await queue.SnapshotAsync();
        snapshot.Single(j => j.Id == jobs[0].Id).State.Should().Be(TransferJobStates.Canceled);
        snapshot.Single(j => j.Id == jobs[1].Id).State.Should().Be(TransferJobStates.Paused);
    }
}
