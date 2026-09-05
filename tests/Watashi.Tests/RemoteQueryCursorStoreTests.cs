using FluentAssertions;
using Watashi.Server.Services;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Tests;

public sealed class RemoteQueryCursorStoreTests
{
    private static readonly RemoteQueryScope Scope = new(
        PermissionId: 7,
        HostId: 11,
        ShareId: 13,
        RootPath: "/allowed",
        HostName: "host",
        ShareName: "share");

    [Fact]
    public void Cursor_pages_are_stable_and_idempotent()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));
        var store = new RemoteQueryCursorStore(clock, Enumerable.Repeat((byte)0x42, 32).ToArray());
        var source = new List<FileEntry>
        {
            new() { Name = "a" },
            new() { Name = "b" },
            new() { Name = "c" },
            new() { Name = "d" },
            new() { Name = "e" },
        };
        var firstRead = store.AddList(1, Scope, "/allowed", "name", source, 5, false, false, null);
        var first = store.GetListPage(firstRead, 2);

        source.Clear();
        var secondA = store.GetListPage(store.OpenList(first.NextCursor!, 1), 2);
        // The caller-provided continuation limit is ignored; the signed first-page width wins.
        var secondB = store.GetListPage(store.OpenList(first.NextCursor!, 1), 1);

        first.Entries.Select(x => x.Name).Should().Equal("a", "b");
        secondA.Entries.Select(x => x.Name).Should().Equal("c", "d");
        secondB.Entries.Select(x => x.Name).Should().Equal("c", "d");
        secondA.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public void Cursor_rejects_tampering_and_a_different_user()
    {
        var store = CreateStore();
        var read = store.AddList(1, Scope, "/allowed", null,
            new[] { new FileEntry { Name = "a" }, new FileEntry { Name = "b" } },
            2, false, false, null);
        var cursor = store.GetListPage(read, 1).NextCursor!;
        var pieces = cursor.Split('.');
        pieces[1] = (pieces[1][0] == 'A' ? 'B' : 'A') + pieces[1][1..];
        var tampered = string.Join('.', pieces);

        var tamper = () => store.OpenList(tampered, 1);
        tamper.Should().Throw<RemoteQueryCursorException>()
            .Where(x => x.StatusCode == 400 && x.Code == "cursor_signature_invalid");

        var otherUser = () => store.OpenList(cursor, 2);
        otherUser.Should().Throw<RemoteQueryCursorException>()
            .Where(x => x.StatusCode == 403 && x.Code == "cursor_owner_mismatch");
    }

    [Fact]
    public void Cursor_from_a_previous_process_is_reported_as_gone()
    {
        var key = Enumerable.Repeat((byte)0x34, 32).ToArray();
        var firstProcess = new RemoteQueryCursorStore(TimeProvider.System, key);
        var read = firstProcess.AddList(1, Scope, "/allowed", null,
            new[] { new FileEntry { Name = "a" }, new FileEntry { Name = "b" } },
            2, false, false, null);
        var cursor = firstProcess.GetListPage(read, 1).NextCursor!;
        var restartedProcess = new RemoteQueryCursorStore(TimeProvider.System, key);

        var act = () => restartedProcess.OpenList(cursor, 1);

        act.Should().Throw<RemoteQueryCursorException>()
            .Where(x => x.StatusCode == 410 && x.Code == "cursor_expired");
    }

    [Fact]
    public void Cursor_expires_at_its_fixed_deadline()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));
        var store = new RemoteQueryCursorStore(clock, Enumerable.Repeat((byte)0x21, 32).ToArray());
        var read = store.AddList(1, Scope, "/allowed", null,
            new[] { new FileEntry { Name = "a" }, new FileEntry { Name = "b" } },
            2, false, false, null);
        var cursor = store.GetListPage(read, 1).NextCursor!;

        clock.Advance(RemoteQueryCursorStore.SnapshotLifetime);
        var expired = () => store.OpenList(cursor, 1);

        expired.Should().Throw<RemoteQueryCursorException>()
            .Where(x => x.StatusCode == 410 && x.Code == "cursor_expired");
    }

    [Fact]
    public void Snapshot_accepts_one_hundred_thousand_entries_with_bounded_pages()
    {
        var store = CreateStore();
        var entries = Enumerable.Range(0, RemoteSearchService.MaxListEntries)
            .Select(i => new FileEntry { Name = $"entry-{i:D6}" })
            .ToArray();
        var read = store.AddList(1, Scope, "/allowed", "name", entries,
            entries.Length, false, false, null);

        var first = store.GetListPage(read, RemoteSearchService.MaxPageSize);
        var second = store.GetListPage(store.OpenList(first.NextCursor!, 1), RemoteSearchService.MaxPageSize);

        first.Entries.Should().HaveCount(RemoteSearchService.MaxPageSize);
        second.Entries.Should().HaveCount(RemoteSearchService.MaxPageSize);
        first.TotalCount.Should().Be(RemoteSearchService.MaxListEntries);
        first.LoadedCount.Should().Be(RemoteSearchService.MaxPageSize);
        first.HasMore.Should().BeTrue();
    }

    [Theory]
    [InlineData(1999)]
    [InlineData(2000)]
    [InlineData(2001)]
    [InlineData(4001)]
    public void List_2000_entry_pages_cover_boundaries_without_duplicates(int count)
    {
        var store = CreateStore();
        var entries = Enumerable.Range(0, count).Select(i => new FileEntry { Name = $"item-{i:D5}" }).ToArray();
        var read = store.AddList(1, Scope, "/allowed", "name", entries, count, false, false, null);
        var page = store.GetListPage(read, 2000);
        page.Entries.Should().HaveCount(Math.Min(count, 2000));
        var names = new List<string>();
        while (true)
        {
            names.AddRange(page.Entries.Select(x => x.Name));
            page.LoadedCount.Should().Be(names.Count);
            page.TotalCount.Should().Be(count);
            page.HasMore.Should().Be(names.Count < count);
            if (!page.HasMore) { page.NextCursor.Should().BeNull(); break; }
            var continuation = store.OpenList(page.NextCursor!, 1);
            // Continuations retain the initial 2000 width, even for an old client's 500 request.
            var next = store.GetListPage(continuation, 500);
            next.Entries.Should().HaveCount(Math.Min(count - names.Count, 2000));
            store.GetListPage(continuation, 1).Entries.Select(x => x.Name)
                .Should().Equal(next.Entries.Select(x => x.Name));
            page = next;
        }
        names.Should().Equal(entries.Select(x => x.Name));
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Search_keeps_500_limit_and_rejects_list_cursors()
    {
        var store = CreateStore();
        var results = Enumerable.Range(0, 501).Select(_ => new RemoteSearchResult()).ToArray();
        var read = store.AddSearch(1, "item", new[] { Scope }, results, 501, 501, false, null, Array.Empty<RemoteQueryWarning>());
        var first = store.GetSearchPage(read, 500);
        first.Results.Should().HaveCount(500);
        store.GetSearchPage(store.OpenSearch(first.NextCursor!, 1), 2000).Results.Should().ContainSingle();
        var tooLarge = () => store.GetSearchPage(read, 501);
        tooLarge.Should().Throw<ArgumentOutOfRangeException>();
        var wrongKind = () => store.OpenList(first.NextCursor!, 1);
        wrongKind.Should().Throw<RemoteQueryCursorException>().Where(x => x.Code == "cursor_kind_mismatch");
        var list = store.AddList(1, Scope, "/allowed", null,
            Enumerable.Range(0, 2001).Select(_ => new FileEntry()).ToArray(), 2001, false, false, null);
        var listCursor = store.GetListPage(list, 2000).NextCursor!;
        var wrongSearch = () => store.OpenSearch(listCursor, 1);
        wrongSearch.Should().Throw<RemoteQueryCursorException>().Where(x => x.Code == "cursor_kind_mismatch");
        var tooLargeList = () => store.GetListPage(list, 2001);
        tooLargeList.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static RemoteQueryCursorStore CreateStore() => new(
        TimeProvider.System,
        Enumerable.Repeat((byte)0x11, 32).ToArray());

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
