using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Tests;

public sealed class RemoteSearchServiceTests
{
    [Fact]
    public async Task Search_does_not_follow_reparse_directories()
    {
        var scope = Scope(1, "/root");
        var lister = new FakeLister();
        lister.Add(1, "/root",
            Directory("needle-link", reparse: true),
            Directory("folder"));
        lister.Add(1, "/root/folder", File("needle.txt"));
        var service = new RemoteSearchService(lister);

        var result = await service.SearchAsync(
            new[] { scope }, "needle", 100, 100, 30, CancellationToken.None);

        result.Results.Select(x => x.FullPath)
            .Should().Equal("/root/needle-link", "/root/folder/needle.txt");
        lister.Calls.Should().Contain((1, "/root/folder"));
        lister.Calls.Should().NotContain((1, "/root/needle-link"));
    }

    [Fact]
    public async Task Agent_supplied_separator_names_cannot_escape_the_permission_root()
    {
        var scope = Scope(1, "/root");
        var lister = new FakeLister();
        lister.Add(1, "/root",
            Directory("../needle-outside"),
            File("sub/needle.txt"),
            File("needle-safe.txt"));
        var service = new RemoteSearchService(lister);

        var result = await service.SearchAsync(
            new[] { scope }, "needle", 100, 100, 30, CancellationToken.None);

        result.Results.Should().ContainSingle(x => x.FullPath == "/root/needle-safe.txt");
        result.Results.Should().OnlyContain(x => PathHelper.IsPathWithin(scope.RootPath, x.FullPath));
        lister.Calls.Should().ContainSingle();
        result.ScannedCount.Should().Be(3);
    }

    [Fact]
    public async Task Partial_failure_continues_without_leaking_exception_or_path()
    {
        var failed = Scope(1, "/classified");
        var available = Scope(2, "/public");
        var lister = new FakeLister();
        lister.Fail(1, "/classified", new IOException(
            "server=10.20.30.40 path=/classified/secret budget=98765"));
        lister.Add(2, "/public", File("needle.txt"));
        var service = new RemoteSearchService(lister);

        var result = await service.SearchAsync(
            new[] { failed, available }, "needle", 100, 100, 30, CancellationToken.None);

        result.Results.Should().ContainSingle(x => x.FullPath == "/public/needle.txt");
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be("partial_failure");
        var warning = result.Warnings.Should().ContainSingle().Subject;
        warning.PermissionId.Should().Be(1);
        warning.Code.Should().Be("scope_unavailable");
        warning.Message.Should().NotContain("classified");
        warning.Message.Should().NotContain("10.20.30.40");
        warning.Message.Should().NotContain("98765");
    }

    [Fact]
    public async Task Scan_and_result_caps_are_hard_limits()
    {
        var scope = Scope(1, "/root");
        var lister = new FakeLister();
        lister.Add(1, "/root", File("needle-a"), File("needle-b"), File("needle-c"));
        var service = new RemoteSearchService(lister);

        var scanLimited = await service.SearchAsync(
            new[] { scope }, "absent", 2, 100, 30, CancellationToken.None);
        var resultLimited = await service.SearchAsync(
            new[] { scope }, "needle", 100, 1, 30, CancellationToken.None);

        scanLimited.ScannedCount.Should().Be(2);
        scanLimited.TruncationReason.Should().Be("scan_limit");
        resultLimited.Results.Should().HaveCount(1);
        resultLimited.TruncationReason.Should().Be("result_limit");
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        var lister = new FakeLister();
        var service = new RemoteSearchService(lister);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => service.SearchAsync(
            new[] { Scope(1, "/root") }, "needle", 100, 100, 30, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Server_timeout_returns_a_bounded_partial_result()
    {
        var service = new RemoteSearchService(new BlockingLister());

        var result = await service.SearchAsync(
            new[] { Scope(1, "/root") }, "needle", 100, 100, 1, CancellationToken.None);

        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be("timeout");
        result.Results.Should().BeEmpty();
    }

    [Fact]
    public void Readable_scopes_are_coalesced_only_on_segment_boundaries()
    {
        var locations = new[]
        {
            Location(2, 10, 20, "/team", read: true),
            Location(3, 10, 20, "/team/sub", read: true),
            Location(4, 10, 20, "/teams", read: true),
            Location(5, 10, 21, "/team/sub", read: true),
            Location(6, 10, 22, "/ignored", read: false),
        };

        var scopes = RemoteSearchService.CoalesceReadableScopes(locations);

        scopes.Select(x => (x.PermissionId, x.ShareId, x.RootPath)).Should().Equal(
            (2, 20, "/team"),
            (4, 20, "/teams"),
            (5, 21, "/team/sub"));
    }

    [Fact]
    public void Cursor_scope_reauthorization_requires_same_active_permission_and_root()
    {
        var scope = Scope(7, "/allowed");

        RemoteSearchService.IsScopeStillAuthorized(scope, new[]
            {
                Location(7, scope.HostId, scope.ShareId, "/allowed", read: true),
            }).Should().BeTrue();
        RemoteSearchService.IsScopeStillAuthorized(scope, new[]
            {
                Location(7, scope.HostId, scope.ShareId, "/narrower", read: true),
            }).Should().BeFalse();
        RemoteSearchService.IsScopeStillAuthorized(scope, new[]
            {
                Location(8, scope.HostId, scope.ShareId, "/allowed", read: true),
            }).Should().BeFalse();
        RemoteSearchService.IsScopeStillAuthorized(scope, new[]
            {
                Location(7, scope.HostId, scope.ShareId, "/allowed", read: false),
            }).Should().BeFalse();
    }

    private static RemoteQueryScope Scope(int permissionId, string root) => new(
        permissionId,
        HostId: 10,
        ShareId: 20 + permissionId,
        RootPath: PathHelper.NormalizePath(root),
        HostName: "host",
        ShareName: "share");

    private static LocationDto Location(
        int permissionId,
        int hostId,
        int shareId,
        string path,
        bool read) => new()
        {
            PermissionId = permissionId,
            HostId = hostId,
            ShareId = shareId,
            Path = path,
            HostName = "host",
            ShareName = "share",
            Permissions = new LocationPermissions { Read = read },
        };

    private static FileEntry File(string name) => new()
    {
        Name = name,
        Type = FileEntryTypes.File,
    };

    private static FileEntry Directory(string name, bool reparse = false) => new()
    {
        Name = name,
        Type = FileEntryTypes.Directory,
        IsReparsePoint = reparse,
    };

    private sealed class FakeLister : IRemoteDirectoryLister
    {
        private readonly Dictionary<(int PermissionId, string Path), object> _responses = new();
        public List<(int PermissionId, string Path)> Calls { get; } = new();

        public void Add(int permissionId, string path, params FileEntry[] entries) =>
            _responses[(permissionId, PathHelper.NormalizePath(path))] = entries;

        public void Fail(int permissionId, string path, Exception exception) =>
            _responses[(permissionId, PathHelper.NormalizePath(path))] = exception;

        public Task<IReadOnlyList<FileEntry>> ListAsync(
            RemoteQueryScope scope,
            string path,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var key = (scope.PermissionId, PathHelper.NormalizePath(path));
            Calls.Add(key);
            if (!_responses.TryGetValue(key, out var response))
                throw new InvalidOperationException("Unexpected traversal: " + key);
            if (response is Exception exception) throw exception;
            return Task.FromResult<IReadOnlyList<FileEntry>>((FileEntry[])response);
        }
    }

    private sealed class BlockingLister : IRemoteDirectoryLister
    {
        public async Task<IReadOnlyList<FileEntry>> ListAsync(
            RemoteQueryScope scope,
            string path,
            CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Array.Empty<FileEntry>();
        }
    }
}
