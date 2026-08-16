using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Tests;

public class TransferQueueServiceTests
{
    [Fact]
    public async Task Upload_resumes_from_server_offset_and_completes()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        var bytes = Enumerable.Range(0, 37).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        var protocol = new FakeProtocol { InitialUploadOffset = 11 };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();

        var id = await queue.EnqueueUploadAsync(
            source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        var completed = await WaitForStateAsync(queue, id, TransferJobStates.Completed);

        completed.BytesTransferred.Should().Be(bytes.Length);
        protocol.UploadOffsets.Should().NotBeEmpty();
        protocol.UploadOffsets[0].Should().Be(11);
        protocol.CreatedRequest!.Sha256.Should().Be(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public async Task Download_resumes_matching_partial_file_and_commits_atomically()
    {
        using var temp = new TemporaryDirectory();
        var bytes = Enumerable.Range(0, 29).Select(i => (byte)(200 - i)).ToArray();
        var protocol = new FakeProtocol { DownloadBytes = bytes, DownloadEtag = "\"v1\"" };
        var storePath = Path.Combine(temp.Path, "queue.json");
        var destination = Path.Combine(temp.Path, "download.bin");
        var job = ValidDownload(destination, bytes.Length, protocol.DownloadEtag, Hash(bytes));
        var partial = Path.Combine(temp.Path, $".download.bin.{job.Id}.watashi-part");
        await File.WriteAllBytesAsync(partial, bytes[..9]);
        var store = new TransferQueueStore(storePath);
        await store.SaveAsync(new[] { job });
        await using var queue = new TransferQueueService(store, protocol);
        await queue.InitializeAsync();

        var completed = await WaitForStateAsync(queue, job.Id, TransferJobStates.Completed);

        completed.BytesTransferred.Should().Be(bytes.Length);
        (await File.ReadAllBytesAsync(destination)).Should().BeEquivalentTo(bytes);
        protocol.DownloadOffsets.Should().ContainSingle().Which.Should().Be(9);
        File.Exists(partial).Should().BeFalse();
    }

    [Fact]
    public async Task Changed_download_etag_discards_stale_partial()
    {
        using var temp = new TemporaryDirectory();
        var bytes = new byte[] { 5, 6, 7, 8 };
        var protocol = new FakeProtocol { DownloadBytes = bytes, DownloadEtag = "\"new\"" };
        var destination = Path.Combine(temp.Path, "download.bin");
        var job = ValidDownload(destination, bytes.Length, "\"old\"", Hash(bytes));
        var partial = Path.Combine(temp.Path, $".download.bin.{job.Id}.watashi-part");
        await File.WriteAllBytesAsync(partial, new byte[] { 1, 2 });
        var store = new TransferQueueStore(Path.Combine(temp.Path, "queue.json"));
        await store.SaveAsync(new[] { job });
        await using var queue = new TransferQueueService(store, protocol);
        await queue.InitializeAsync();

        await WaitForStateAsync(queue, job.Id, TransferJobStates.Completed);

        protocol.DownloadOffsets.Should().ContainSingle().Which.Should().Be(0);
        (await File.ReadAllBytesAsync(destination)).Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Download_total_checksum_mismatch_discards_partial_and_never_commits_target()
    {
        using var temp = new TemporaryDirectory();
        var bytes = new byte[] { 5, 6, 7, 8 };
        var protocol = new FakeProtocol
        {
            DownloadBytes = bytes,
            CorruptDownloadBody = true,
        };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var destination = Path.Combine(temp.Path, "download.bin");
        var id = await queue.EnqueueDownloadAsync(
            1, 2, "/download.bin", destination, bytes.Length, TransferConflictPolicies.Overwrite);

        var failed = await WaitForStateAsync(queue, id, TransferJobStates.Failed);

        failed.LastError.Should().Contain("ファイル全体のSHA-256");
        failed.BytesTransferred.Should().Be(0);
        File.Exists(destination).Should().BeFalse();
        Directory.EnumerateFiles(temp.Path, "*.watashi-part").Should().BeEmpty();
    }

    [Fact]
    public async Task Failure_is_persisted_and_only_explicit_retry_runs_again()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var protocol = new FakeProtocol
        {
            FailCreatesRemaining = 1,
            FailCreateStatus = HttpStatusCode.Forbidden,
        };
        var storePath = Path.Combine(temp.Path, "queue.json");
        await using var queue = new TransferQueueService(new TransferQueueStore(storePath), protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(source, 1, 2, "/a.bin", TransferConflictPolicies.Overwrite);

        var failed = await WaitForStateAsync(queue, id, TransferJobStates.Failed);
        failed.AttemptCount.Should().Be(1);
        await Task.Delay(100);
        protocol.CreateCalls.Should().Be(1);

        await queue.RetryFailedAsync();
        var completed = await WaitForStateAsync(queue, id, TransferJobStates.Completed);
        completed.AttemptCount.Should().Be(2);
        protocol.CreateCalls.Should().Be(2);
    }

    [Fact]
    public async Task Transient_server_failure_is_retried_automatically_with_backoff_state()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var protocol = new FakeProtocol { FailCreatesRemaining = 1 };
        await using var queue = new TransferQueueService(
            new TransferQueueStore(Path.Combine(temp.Path, "queue.json")),
            protocol,
            retryDelayFactory: _ => TimeSpan.FromMilliseconds(50));
        await queue.InitializeAsync();

        var id = await queue.EnqueueUploadAsync(
            source, 1, 2, "/retry.bin", TransferConflictPolicies.Overwrite);
        var completed = await WaitForStateAsync(queue, id, TransferJobStates.Completed);

        completed.AttemptCount.Should().Be(2);
        protocol.CreateCalls.Should().Be(2);
    }

    [Fact]
    public async Task Skip_policy_does_not_overwrite_existing_remote_file()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1 });
        var protocol = new FakeProtocol { RemoteExists = true };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(source, 1, 2, "/a.bin", TransferConflictPolicies.Skip);

        var skipped = await WaitForStateAsync(queue, id, TransferJobStates.Skipped);

        skipped.LastError.Should().BeNull();
        protocol.CreateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Enqueue_is_allowed_while_another_job_is_running()
    {
        using var temp = new TemporaryDirectory();
        var first = Path.Combine(temp.Path, "first.bin");
        var second = Path.Combine(temp.Path, "second.bin");
        await File.WriteAllBytesAsync(first, new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(second, new byte[] { 4, 5, 6 });
        var protocol = new FakeProtocol { BlockUploads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var queue = new TransferQueueService(
            new TransferQueueStore(Path.Combine(temp.Path, "queue.json")), protocol, maxConcurrent: 1);
        await queue.InitializeAsync();
        var firstId = await queue.EnqueueUploadAsync(first, 1, 2, "/first.bin", TransferConflictPolicies.Overwrite);
        await WaitForStateAsync(queue, firstId, TransferJobStates.Running);

        var secondId = await queue.EnqueueUploadAsync(second, 1, 2, "/second.bin", TransferConflictPolicies.Overwrite);
        (await queue.SnapshotAsync()).Single(x => x.Id == secondId).State.Should().Be(TransferJobStates.Queued);

        protocol.BlockUploads.SetResult();
        await WaitForStateAsync(queue, firstId, TransferJobStates.Completed);
        await WaitForStateAsync(queue, secondId, TransferJobStates.Completed);
    }

    [Fact]
    public async Task Cancel_running_upload_cancels_remote_session_and_keeps_resumable_record()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var protocol = new FakeProtocol
        {
            BlockUploads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        await protocol.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await queue.CancelAsync(id);
        var canceled = await WaitForStateAsync(queue, id, TransferJobStates.Canceled);

        canceled.ServerSessionId.Should().BeNull();
        canceled.ServerSessionGeneration.Should().Be(1);
        protocol.CancelCalls.Should().Be(1);
    }

    [Fact]
    public async Task Resume_is_rejected_until_active_cancel_cleanup_finishes()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var protocol = new FakeProtocol
        {
            BlockUploads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockCancels = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(
            source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        await protocol.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await queue.CancelAsync(id);
        (await queue.SnapshotAsync()).Single(x => x.Id == id).State.Should().Be(TransferJobStates.Canceling);
        await queue.ResumeAsync(id);
        (await queue.SnapshotAsync()).Single(x => x.Id == id).State.Should().Be(TransferJobStates.Canceling);

        protocol.BlockCancels.SetResult();
        await WaitForStateAsync(queue, id, TransferJobStates.Canceled);
    }

    [Fact]
    public async Task Mismatched_cancel_response_is_not_accepted_as_completed()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var protocol = new FakeProtocol
        {
            BlockUploads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReturnMismatchedCancelSession = true,
        };
        await using var queue = Queue(temp, protocol);
        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(
            source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        await protocol.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await queue.CancelAsync(id);
        var paused = await WaitForStateAsync(queue, id, TransferJobStates.Paused);

        paused.ServerSessionId.Should().NotBeNull();
        paused.LastError.Should().Contain("中止結果");
    }

    [Fact]
    public async Task Jobs_targeting_same_remote_path_run_serially()
    {
        using var temp = new TemporaryDirectory();
        var first = Path.Combine(temp.Path, "first.bin");
        var second = Path.Combine(temp.Path, "second.bin");
        await File.WriteAllBytesAsync(first, new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(second, new byte[] { 4, 5, 6 });
        var protocol = new FakeProtocol
        {
            BlockUploads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var queue = new TransferQueueService(
            new TransferQueueStore(Path.Combine(temp.Path, "queue.json")), protocol, maxConcurrent: 2);
        await queue.InitializeAsync();
        var firstId = await queue.EnqueueUploadAsync(
            first, 1, 2, "/same.bin", TransferConflictPolicies.Overwrite);
        await protocol.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondId = await queue.EnqueueUploadAsync(
            second, 1, 2, "/same.bin", TransferConflictPolicies.Overwrite);
        await Task.Delay(100);

        (await queue.SnapshotAsync()).Single(x => x.Id == secondId).State.Should().Be(TransferJobStates.Queued);
        protocol.UploadOffsets.Should().ContainSingle();
        protocol.BlockUploads.SetResult();
        await WaitForStateAsync(queue, firstId, TransferJobStates.Completed);
        await WaitForStateAsync(queue, secondId, TransferJobStates.Completed);
    }

    [Fact]
    public async Task Download_recovers_when_final_file_was_moved_before_completed_state_was_saved()
    {
        using var temp = new TemporaryDirectory();
        var bytes = Enumerable.Range(0, 9).Select(x => (byte)x).ToArray();
        var destination = Path.Combine(temp.Path, "download.bin");
        await File.WriteAllBytesAsync(destination, bytes);
        var protocol = new FakeProtocol { DownloadBytes = bytes, DownloadEtag = "\"v1\"" };
        var job = ValidDownload(destination, bytes.Length, protocol.DownloadEtag, Hash(bytes));
        job.BytesTransferred = bytes.Length;
        job.ConflictPolicy = TransferConflictPolicies.Ask;
        var store = new TransferQueueStore(Path.Combine(temp.Path, "queue.json"));
        await store.SaveAsync(new[] { job });
        await using var queue = new TransferQueueService(store, protocol);

        await queue.InitializeAsync();
        await WaitForStateAsync(queue, job.Id, TransferJobStates.Completed);

        protocol.DownloadOffsets.Should().BeEmpty();
        (await File.ReadAllBytesAsync(destination)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Faulty_queue_changed_subscriber_does_not_stop_worker()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        await using var queue = Queue(temp, new FakeProtocol());
        queue.QueueChanged += _ => throw new InvalidOperationException("UI failure");

        await queue.InitializeAsync();
        var id = await queue.EnqueueUploadAsync(source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);

        await WaitForStateAsync(queue, id, TransferJobStates.Completed);
    }

    [Fact]
    public async Task Failed_enqueue_persistence_rolls_back_hidden_in_memory_job()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var store = new ControllableStore(Path.Combine(temp.Path, "queue.json"));
        await using var queue = new TransferQueueService(store, new FakeProtocol());
        await queue.InitializeAsync();
        store.FailAllSaves = true;

        var act = () => queue.EnqueueUploadAsync(
            source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);

        await act.Should().ThrowAsync<IOException>();
        (await queue.SnapshotAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Pump_recovers_after_transient_claim_persistence_failure()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var store = new ControllableStore(Path.Combine(temp.Path, "queue.json"))
        {
            FailSaveNumber = 2,
        };
        await using var queue = new TransferQueueService(store, new FakeProtocol());
        var errors = new List<string>();
        queue.QueueError += errors.Add;
        await queue.InitializeAsync();

        var id = await queue.EnqueueUploadAsync(
            source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        var completed = await WaitForStateAsync(queue, id, TransferJobStates.Completed, timeoutMs: 7000);

        completed.AttemptCount.Should().Be(1);
        errors.Should().ContainSingle(x => x.Contains("保存先"));
    }

    [Fact]
    public async Task Retry_timer_recovers_after_transient_persistence_failure()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var store = new ControllableStore(Path.Combine(temp.Path, "queue.json"))
        {
            // enqueue=1, first claim=2, content hash=3, retry_waiting=4, due promotion=5
            FailSaveNumber = 5,
        };
        var protocol = new FakeProtocol { FailCreatesRemaining = 1 };
        await using var queue = new TransferQueueService(
            store, protocol, retryDelayFactory: _ => TimeSpan.FromMilliseconds(25));
        await queue.InitializeAsync();

        var id = await queue.EnqueueUploadAsync(
            source, 1, 2, "/retry.bin", TransferConflictPolicies.Overwrite);
        var completed = await WaitForStateAsync(queue, id, TransferJobStates.Completed, timeoutMs: 7000);

        completed.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Dispose_waits_for_in_progress_initialization_and_releases_queue_lease()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "source.bin");
        await File.WriteAllBytesAsync(source, new byte[] { 1 });
        var store = new ControllableStore(Path.Combine(temp.Path, "queue.json"))
        {
            BlockLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var queue = new TransferQueueService(store, new FakeProtocol());

        var initialize = queue.InitializeAsync();
        await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = queue.DisposeAsync().AsTask();
        await Task.Delay(50);
        dispose.IsCompleted.Should().BeFalse();

        store.BlockLoad.SetResult();
        await initialize;
        await dispose;

        var act = () => queue.EnqueueUploadAsync(
            source, 1, 2, "/source.bin", TransferConflictPolicies.Overwrite);
        await act.Should().ThrowAsync<ObjectDisposedException>();
        using var nextLease = new TransferQueueStore(Path.Combine(temp.Path, "queue.json"))
            .AcquireExclusiveLease();
    }

    private static TransferQueueService Queue(TemporaryDirectory temp, ITransferProtocol protocol) => new(
        new TransferQueueStore(Path.Combine(temp.Path, "queue.json")), protocol);

    private static TransferJobRecord ValidDownload(string destination, long size, string etag, string sha256) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Direction = TransferDirections.Download,
        LocalPath = destination,
        HostId = 1,
        ShareId = 2,
        RemotePath = "/download.bin",
        TotalBytes = size,
        BytesTransferred = 9,
        State = TransferJobStates.Queued,
        ConflictPolicy = TransferConflictPolicies.Overwrite,
        RemoteETag = etag,
        ContentSha256 = sha256,
    };

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<TransferJobRecord> WaitForStateAsync(
        TransferQueueService queue, string id, string expected, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var job = (await queue.SnapshotAsync()).Single(x => x.Id == id);
            if (job.State == expected) return job;
            if (job.State == TransferJobStates.Failed && expected != TransferJobStates.Failed)
                throw new Xunit.Sdk.XunitException($"ジョブが失敗しました: {job.LastError}");
            await Task.Delay(15);
        }
        var current = (await queue.SnapshotAsync()).Single(x => x.Id == id);
        throw new TimeoutException($"Expected {expected}, actual {current.State}: {current.LastError}");
    }

    private sealed class FakeProtocol : ITransferProtocol
    {
        private readonly Guid _sessionId = Guid.NewGuid();
        private long _offset;
        public int InitialUploadOffset { get; init; }
        public int FailCreatesRemaining { get; set; }
        public HttpStatusCode FailCreateStatus { get; init; } = HttpStatusCode.ServiceUnavailable;
        public int CreateCalls { get; private set; }
        public bool RemoteExists { get; init; }
        public byte[] DownloadBytes { get; init; } = Array.Empty<byte>();
        public string DownloadEtag { get; init; } = "\"etag\"";
        public bool CorruptDownloadBody { get; init; }
        public TaskCompletionSource? BlockUploads { get; init; }
        public TaskCompletionSource? BlockCancels { get; init; }
        public bool ReturnMismatchedCancelSession { get; init; }
        public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelCalls { get; private set; }
        public CreateUploadSessionRequest? CreatedRequest { get; private set; }
        public List<long> UploadOffsets { get; } = new();
        public List<long> DownloadOffsets { get; } = new();

        public Task<UploadSessionDto> CreateUploadSessionAsync(CreateUploadSessionRequest request, CancellationToken ct = default)
        {
            CreateCalls++;
            CreatedRequest = request;
            if (FailCreatesRemaining-- > 0)
                return Task.FromException<UploadSessionDto>(new ApiException(FailCreateStatus, "temporary"));
            _offset = InitialUploadOffset;
            return Task.FromResult(Session("active"));
        }

        public Task<UploadSessionDto> GetUploadSessionAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(Session("active"));

        public async Task<UploadSessionDto> UploadChunkAsync(
            Guid sessionId, long offset, byte[] buffer, int count, string chunkSha256, CancellationToken ct = default)
        {
            UploadOffsets.Add(offset);
            UploadStarted.TrySetResult();
            if (BlockUploads is not null) await BlockUploads.Task.WaitAsync(ct);
            _offset = offset + count;
            return Session("active");
        }

        public Task<UploadSessionDto> CompleteUploadSessionAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult(Session("completed"));

        public async Task<UploadSessionDto> CancelUploadSessionAsync(Guid sessionId, CancellationToken ct = default)
        {
            CancelCalls++;
            if (BlockCancels is not null) await BlockCancels.Task.WaitAsync(ct);
            var response = Session("cancelled");
            return ReturnMismatchedCancelSession
                ? response with { SessionId = Guid.NewGuid(), Status = "completed" }
                : response;
        }

        public Task<TransferDownloadMetadataDto> GetDownloadMetadataV2Async(
            int hostId, int shareId, string path, CancellationToken ct = default)
            => Task.FromResult(new TransferDownloadMetadataDto
            {
                Exists = DownloadBytes.Length > 0 || RemoteExists,
                Type = "file",
                Size = DownloadBytes.LongLength,
                ETag = DownloadEtag,
                Sha256 = Convert.ToHexString(SHA256.HashData(DownloadBytes)).ToLowerInvariant(),
            });

        public async Task DownloadRangeV2Async(
            int hostId, int shareId, string path, long offset, int length, string? etag,
            Stream output, CancellationToken ct = default)
        {
            etag.Should().Be(DownloadEtag);
            DownloadOffsets.Add(offset);
            var data = DownloadBytes.AsMemory((int)offset, length).ToArray();
            if (CorruptDownloadBody && data.Length > 0) data[0] ^= 0xff;
            await output.WriteAsync(data, ct);
        }

        private UploadSessionDto Session(string status) => new()
        {
            SessionId = _sessionId,
            Status = status,
            HostId = CreatedRequest?.HostId ?? 1,
            ShareId = CreatedRequest?.ShareId ?? 2,
            Path = CreatedRequest?.Path ?? "/source.bin",
            TotalSize = CreatedRequest?.TotalSize ?? _offset,
            UploadedOffset = _offset,
            Sha256 = CreatedRequest?.Sha256 ?? new string('0', 64),
            Overwrite = CreatedRequest?.Overwrite ?? true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "watashi-transfer-queue-service-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private sealed class ControllableStore(string path) : TransferQueueStore(path)
    {
        private int _saveCount;
        public bool FailAllSaves { get; set; }
        public int? FailSaveNumber { get; init; }
        public TaskCompletionSource? BlockLoad { get; init; }
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<List<TransferJobRecord>> LoadAsync(CancellationToken ct = default)
        {
            LoadStarted.TrySetResult();
            if (BlockLoad is not null) await BlockLoad.Task.WaitAsync(ct);
            return await base.LoadAsync(ct);
        }

        public override Task SaveAsync(IReadOnlyCollection<TransferJobRecord> jobs, CancellationToken ct = default)
        {
            var number = Interlocked.Increment(ref _saveCount);
            if (FailAllSaves || number == FailSaveNumber)
                return Task.FromException(new IOException("simulated persistence failure"));
            return base.SaveAsync(jobs, ct);
        }
    }
}
