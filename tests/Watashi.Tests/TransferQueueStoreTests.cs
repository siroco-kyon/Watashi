using FluentAssertions;
using Watashi.Client.Services;

namespace Watashi.Tests;

public class TransferQueueStoreTests
{
    [Fact]
    public async Task Save_and_load_round_trips_without_credentials()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "queue.json");
        var store = new TransferQueueStore(path);
        var job = ValidJob();

        await store.SaveAsync(new[] { job });
        var loaded = await store.LoadAsync();

        loaded.Should().ContainSingle().Which.Id.Should().Be(job.Id);
        var json = await File.ReadAllTextAsync(path);
        json.ToLowerInvariant().Should().NotContain("password")
            .And.NotContain("token");
    }

    [Fact]
    public async Task In_progress_job_is_paused_after_restart()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "queue.json");
        var store = new TransferQueueStore(path);
        var job = ValidJob();
        job.State = TransferJobStates.Running;
        await store.SaveAsync(new[] { job });

        var loaded = await store.LoadAsync();

        loaded.Single().State.Should().Be(TransferJobStates.Paused);
        loaded.Single().LastError.Should().Contain("再開");
    }

    [Fact]
    public async Task Corrupt_file_is_preserved_and_returns_empty_queue()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "queue.json");
        await File.WriteAllTextAsync(path, "{not-json");
        var store = new TransferQueueStore(path);

        var loaded = await store.LoadAsync();

        loaded.Should().BeEmpty();
        File.Exists(path).Should().BeFalse();
        Directory.EnumerateFiles(temp.Path, "queue.json.corrupt-*").Should().ContainSingle();
    }

    [Fact]
    public async Task Transient_io_failure_is_reported_without_treating_queue_as_empty()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "queue.json");
        var store = new TransferQueueStore(path);
        await store.SaveAsync(new[] { ValidJob() });
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var act = () => store.LoadAsync();

        await act.Should().ThrowAsync<IOException>();
        File.Exists(path).Should().BeTrue();
        Directory.EnumerateFiles(temp.Path, "queue.json.corrupt-*").Should().BeEmpty();
    }

    [Fact]
    public void Normalize_filters_invalid_and_duplicate_jobs_and_clamps_progress()
    {
        var first = ValidJob();
        first.BytesTransferred = 999;
        var duplicate = ValidJob();
        duplicate.Id = first.Id;
        var invalid = ValidJob();
        invalid.HostId = 0;

        var normalized = TransferQueueStore.NormalizeLoadedJobs(new[] { first, duplicate, invalid });

        normalized.Should().ContainSingle();
        normalized[0].BytesTransferred.Should().Be(normalized[0].TotalBytes);
    }

    [Fact]
    public void User_queue_paths_are_separated_without_using_usernames()
    {
        var first = TransferQueueStore.GetUserQueuePath(10);
        var second = TransferQueueStore.GetUserQueuePath(11);

        first.Should().NotBe(second);
        Path.GetFileName(first).Should().Be("transfer-queue-user-10.json");
        Path.GetDirectoryName(first).Should().Be(Path.GetDirectoryName(second));
    }

    [Fact]
    public void User_queue_paths_are_also_separated_by_server()
    {
        var first = TransferQueueStore.GetUserQueuePath(10, "https://server-a.example/api/");
        var sameNormalized = TransferQueueStore.GetUserQueuePath(10, "HTTPS://SERVER-A.EXAMPLE/api");
        var second = TransferQueueStore.GetUserQueuePath(10, "https://server-b.example/api");

        first.Should().Be(sameNormalized);
        first.Should().NotBe(second);
        Path.GetFileName(first).ToLowerInvariant().Should().NotContain("server-a");
    }

    [Fact]
    public void User_queue_path_preserves_case_sensitive_server_base_path()
    {
        var upperTenant = TransferQueueStore.GetUserQueuePath(10, "https://server.example/TenantA/");
        var lowerTenant = TransferQueueStore.GetUserQueuePath(10, "https://SERVER.EXAMPLE/tenanta");

        upperTenant.Should().NotBe(lowerTenant);
    }

    [Fact]
    public void Exclusive_lease_prevents_two_processes_from_running_the_same_queue()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "queue.json");
        var first = new TransferQueueStore(path);
        var second = new TransferQueueStore(path);

        using var lease = first.AcquireExclusiveLease();
        var act = () => second.AcquireExclusiveLease();

        act.Should().Throw<IOException>().WithMessage("*別のWatashiウィンドウ*");
    }

    private static TransferJobRecord ValidJob() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Direction = TransferDirections.Upload,
        LocalPath = @"C:\data\a.bin",
        HostId = 1,
        ShareId = 2,
        RemotePath = "/a.bin",
        TotalBytes = 100,
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "watashi-transfer-queue-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
