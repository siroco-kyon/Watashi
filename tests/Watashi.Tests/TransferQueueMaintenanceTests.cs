using System.Net;
using FluentAssertions;
using Watashi.Client.Services;

namespace Watashi.Tests;

public partial class TransferQueueServiceTests
{
    [Fact]
    public async Task Maintenance_set_before_initialization_blocks_pump_and_resumes_only_maintenance_jobs()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var jobs = new[] { TransferJobStates.Queued, TransferJobStates.Paused,
            TransferJobStates.Canceled, TransferJobStates.RetryWaiting, TransferJobStates.ConflictWaiting }
            .Select(state => new TransferJobRecord
            {
                LocalPath = source, HostId = 1, ShareId = 2, RemotePath = "/" + state + ".bin",
                State = state, TotalBytes = 3, ConflictPolicy = TransferConflictPolicies.Overwrite,
                NextAttemptAtUtc = state == TransferJobStates.RetryWaiting ? DateTime.UtcNow.AddHours(1) : null,
                AttemptCount = state == TransferJobStates.RetryWaiting ? 3 : 0,
            }).ToArray();
        var store = new TransferQueueStore(Path.Combine(temp.Path, "queue.json"));
        await store.SaveAsync(jobs);
        var protocol = new FakeProtocol();
        await using var queue = new TransferQueueService(store, protocol);
        await queue.SetMaintenanceAsync(true);
        await queue.InitializeAsync();
        var blocked = await queue.SnapshotAsync();
        blocked.Single(x => x.Id == jobs[0].Id).State.Should().Be(TransferJobStates.MaintenanceWaiting);
        blocked.Single(x => x.Id == jobs[3].Id).MaintenanceResumeState.Should().Be(TransferJobStates.RetryWaiting);
        protocol.CreateCalls.Should().Be(0);
        var add = () => queue.EnqueueUploadAsync(source, 1, 2, "/new.bin", TransferConflictPolicies.Ask);
        await add.Should().ThrowAsync<InvalidOperationException>();

        await queue.SetMaintenanceAsync(false);
        await WaitForStateAsync(queue, jobs[0].Id, TransferJobStates.Completed);
        var restored = await queue.SnapshotAsync();
        restored.Single(x => x.Id == jobs[1].Id).State.Should().Be(TransferJobStates.Paused);
        restored.Single(x => x.Id == jobs[2].Id).State.Should().Be(TransferJobStates.Canceled);
        restored.Single(x => x.Id == jobs[3].Id).State.Should().Be(TransferJobStates.RetryWaiting);
        restored.Single(x => x.Id == jobs[3].Id).NextAttemptAtUtc.Should().Be(jobs[3].NextAttemptAtUtc);
        restored.Single(x => x.Id == jobs[3].Id).AttemptCount.Should().Be(3);
        restored.Single(x => x.Id == jobs[4].Id).State.Should().Be(TransferJobStates.ConflictWaiting);
        protocol.CreateCalls.Should().Be(1);
    }

    [Fact]
    public async Task Maintenance_503_holds_without_consuming_automatic_retry_budget()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var protocol = new FakeProtocol { FailCreatesRemaining = 1, FailCreateCode = "maintenance" };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        var held = await WaitForStateAsync(queue, id, TransferJobStates.MaintenanceWaiting);
        held.AttemptCount.Should().Be(0);
        held.NextAttemptAtUtc.Should().BeNull();
        queue.IsMaintenanceBlocked.Should().BeTrue();
        protocol.CreateCalls.Should().Be(1);
        await queue.SetMaintenanceAsync(false);
        var completed = await WaitForStateAsync(queue, id, TransferJobStates.Completed);
        completed.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Maintenance_finishes_inflight_upload_chunk_and_holds_before_commit()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var protocol = new FakeProtocol { BlockUploads = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        await protocol.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.SetMaintenanceAsync(true);
        protocol.BlockUploads.SetResult();
        var held = await WaitForStateAsync(queue, id, TransferJobStates.MaintenanceWaiting);
        held.BytesTransferred.Should().Be(3);
        held.ServerSessionId.Should().NotBeNull();
        protocol.CompleteCalls.Should().Be(0);
        protocol.CancelCalls.Should().Be(0);
        var saved = await new TransferQueueStore(Path.Combine(temp.Path, "queue.json")).LoadAsync();
        saved.Single().State.Should().Be(TransferJobStates.MaintenanceWaiting);
        saved.Single().BytesTransferred.Should().Be(3);
        await queue.SetMaintenanceAsync(false);
        await WaitForStateAsync(queue, id, TransferJobStates.Completed);
        protocol.UploadOffsets.Should().ContainSingle();
        protocol.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task Maintenance_during_upload_commit_records_confirmed_completion()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1 });
        var protocol = new FakeProtocol { BlockCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        await protocol.CompleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.SetMaintenanceAsync(true);
        protocol.BlockCompletes.SetResult();
        await WaitForStateAsync(queue, id, TransferJobStates.Completed);
        await queue.SetMaintenanceAsync(false);
        protocol.CreateCalls.Should().Be(1);
        protocol.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task Maintenance_finishes_verified_download_range_and_holds_before_local_commit()
    {
        using var temp = new TemporaryDirectory();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var protocol = new FakeProtocol { DownloadBytes = bytes };
        await using var queue = Queue(temp, protocol);
        protocol.AfterDownloadRange = () => queue.SetMaintenanceAsync(true);
        await queue.InitializeAsync();
        var destination = Path.Combine(temp.Path, "download.bin");
        var id = await queue.EnqueueDownloadAsync(1, 2, "/download.bin", destination, bytes.Length, TransferConflictPolicies.Overwrite);
        var held = await WaitForStateAsync(queue, id, TransferJobStates.MaintenanceWaiting);
        held.BytesTransferred.Should().Be(bytes.Length);
        File.Exists(destination).Should().BeFalse();
        var partial = Directory.EnumerateFiles(temp.Path, "*.watashi-part").Single();
        (await File.ReadAllBytesAsync(partial)).Should().Equal(bytes);
        protocol.AfterDownloadRange = null;
        await queue.SetMaintenanceAsync(false);
        await WaitForStateAsync(queue, id, TransferJobStates.Completed);
        protocol.DownloadOffsets.Should().ContainSingle();
        (await File.ReadAllBytesAsync(destination)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Failed_download_range_is_truncated_before_maintenance_resume()
    {
        using var temp = new TemporaryDirectory();
        var protocol = new FakeProtocol { DownloadBytes = new byte[] { 4, 5, 6 } };
        protocol.AfterDownloadRange = () => throw new ApiException(HttpStatusCode.ServiceUnavailable, "maintenance", "maintenance");
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var destination = Path.Combine(temp.Path, "download.bin");
        var id = await queue.EnqueueDownloadAsync(1, 2, "/download.bin", destination, 3, TransferConflictPolicies.Overwrite);
        var held = await WaitForStateAsync(queue, id, TransferJobStates.MaintenanceWaiting);
        held.BytesTransferred.Should().Be(0);
        new FileInfo(Directory.EnumerateFiles(temp.Path, "*.watashi-part").Single()).Length.Should().Be(0);
        protocol.AfterDownloadRange = null;
        await queue.SetMaintenanceAsync(false);
        await WaitForStateAsync(queue, id, TransferJobStates.Completed);
        protocol.DownloadOffsets.Should().Equal(0L, 0L);
    }

    [Fact]
    public async Task User_cancellation_during_maintenance_does_not_auto_resume()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1 });
        var protocol = new FakeProtocol { BlockUploads = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        await protocol.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.SetMaintenanceAsync(true);
        await queue.CancelAsync(id);
        await WaitForStateAsync(queue, id, TransferJobStates.Canceled);
        await queue.SetMaintenanceAsync(false);
        (await queue.SnapshotAsync()).Single().State.Should().Be(TransferJobStates.Canceled);
        protocol.CancelCalls.Should().Be(1);
        protocol.CompleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Restart_keeps_persisted_maintenance_holds_until_verified_release()
    {
        using var temp = new TemporaryDirectory();
        var bytes = new byte[] { 1, 2 };
        var job = ValidDownload(Path.Combine(temp.Path, "download.bin"), 2, "\"etag\"", Hash(bytes));
        job.State = TransferJobStates.Running;
        job.MaintenanceResumeState = TransferJobStates.Queued;
        var store = new TransferQueueStore(Path.Combine(temp.Path, "queue.json"));
        await store.SaveAsync(new[] { job });
        var protocol = new FakeProtocol { DownloadBytes = bytes };
        await using var queue = new TransferQueueService(store, protocol);
        await queue.InitializeAsync();
        queue.IsMaintenanceBlocked.Should().BeTrue();
        (await queue.SnapshotAsync()).Single().State.Should().Be(TransferJobStates.MaintenanceWaiting);
        protocol.DownloadOffsets.Should().BeEmpty();
        await queue.SetMaintenanceAsync(false);
        await WaitForStateAsync(queue, job.Id, TransferJobStates.Completed);
    }

    [Fact]
    public async Task Shutdown_during_maintenance_preserves_hold_instead_of_user_pause()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1 });
        var protocol = new FakeProtocol { BlockUploads = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        await queue.EnqueueUploadAsync(source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        await protocol.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.SetMaintenanceAsync(true);
        await queue.DisposeAsync();
        var loaded = await new TransferQueueStore(Path.Combine(temp.Path, "queue.json")).LoadAsync();
        loaded.Single().State.Should().Be(TransferJobStates.MaintenanceWaiting);
        protocol.CancelCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(TransferJobStates.Failed)]
    [InlineData(TransferJobStates.Paused)]
    [InlineData(TransferJobStates.Canceled)]
    public async Task Overwrite_resolution_cannot_restart_non_conflict_jobs(string state)
    {
        using var temp = new TemporaryDirectory();
        var job = ValidDownload(Path.Combine(temp.Path, "download.bin"), 1, "\"etag\"", Hash(new byte[] { 1 }));
        job.State = state;
        job.ConflictPolicy = TransferConflictPolicies.Ask;
        var store = new TransferQueueStore(Path.Combine(temp.Path, "queue.json"));
        await store.SaveAsync(new[] { job });
        await using var queue = new TransferQueueService(store, new FakeProtocol());
        await queue.InitializeAsync();
        await queue.ResolveConflictAndRetryAsync(job.Id, TransferConflictPolicies.Overwrite);
        var unchanged = (await queue.SnapshotAsync()).Single();
        unchanged.State.Should().Be(state);
        unchanged.ConflictPolicy.Should().Be(TransferConflictPolicies.Ask);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Conflict_waiting_retains_source_and_destination_comparison(bool upload)
    {
        using var temp = new TemporaryDirectory();
        var local = Path.Combine(temp.Path, "local.bin");
        await File.WriteAllBytesAsync(local, new byte[] { 1, 2, 3 });
        var protocol = new FakeProtocol { RemoteExists = true, DownloadBytes = new byte[] { 4, 5 } };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = upload
            ? await queue.EnqueueUploadAsync(local, 1, 2, "/source.bin", TransferConflictPolicies.Ask)
            : await queue.EnqueueDownloadAsync(1, 2, "/download.bin", local, 2, TransferConflictPolicies.Ask);
        var conflict = await WaitForStateAsync(queue, id, TransferJobStates.ConflictWaiting);
        conflict.TotalBytes.Should().Be(upload ? 3 : 2);
        conflict.ConflictDestinationSize.Should().Be(upload ? 2 : 3);
        conflict.SourceLastWriteUtc.Should().NotBeNull();
        conflict.ConflictDestinationModifiedUtc.Should().NotBeNull();
        await queue.RetryFailedAsync();
        (await queue.SnapshotAsync()).Single().State.Should().Be(TransferJobStates.ConflictWaiting);
        await queue.ResolveConflictAndRetryAsync(id, TransferConflictPolicies.Skip);
        await WaitForStateAsync(queue, id, TransferJobStates.Skipped);
        (await File.ReadAllBytesAsync(local)).Should().Equal(1, 2, 3);
    }
}
