using FluentAssertions;
using Watashi.Server.Endpoints;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public sealed class RemoteCopyServiceTests
{
    private static readonly FileEndpoints.ExecutionContext Context = new(
        new CifsConnectionInfo("host", 445, "user", "pw", "share"),
        new ExecutionNode { Id = 1, Name = "direct", NodeType = NodeTypes.Direct, IsActive = true });

    [Fact]
    public async Task File_copy_preserves_source_and_uses_safe_rename_on_collision()
    {
        var router = new MemoryRouter();
        router.File("/source/report.txt", "new-content");
        router.File("/target/report.txt", "old-content");
        var service = new RemoteCopyService(router);

        var result = await service.CopyAsync(Request("/source/report.txt", "/target/report.txt", "rename"),
            Context, Context, CancellationToken.None);

        result.TargetPath.Should().Be("/target/report (1).txt");
        result.RenamedForCollision.Should().BeTrue();
        router.Text("/source/report.txt").Should().Be("new-content");
        router.Text("/target/report.txt").Should().Be("old-content");
        router.Text(result.TargetPath).Should().Be("new-content");
    }

    [Fact]
    public async Task Directory_copy_is_published_only_after_all_children_are_copied()
    {
        var router = new MemoryRouter();
        router.Directory("/source/folder");
        router.Directory("/source/folder/sub");
        router.File("/source/folder/a.bin", "aaa");
        router.File("/source/folder/sub/b.bin", "bbbb");
        var service = new RemoteCopyService(router);

        var result = await service.CopyAsync(Request("/source/folder", "/target/folder"),
            Context, Context, CancellationToken.None);

        result.ItemCount.Should().Be(4);
        result.BytesCopied.Should().Be(7);
        router.Exists("/source/folder/a.bin").Should().BeTrue();
        router.Text("/target/folder/a.bin").Should().Be("aaa");
        router.Text("/target/folder/sub/b.bin").Should().Be("bbbb");
        router.Paths.Should().NotContain(path => path.Contains(".watashi-copy-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copy_rejects_destination_inside_source_without_mutation()
    {
        var router = new MemoryRouter();
        router.Directory("/source/folder");
        var service = new RemoteCopyService(router);

        var act = () => service.CopyAsync(
            Request("/source/folder", "/source/folder/nested"), Context, Context, CancellationToken.None);

        await act.Should().ThrowAsync<RemoteCopyException>()
            .Where(ex => ex.Code == "target_inside_source");
        router.Paths.Should().BeEquivalentTo(["/source/folder"]);
    }

    [Fact]
    public async Task Failed_directory_copy_removes_unpublished_temporary_tree()
    {
        var router = new MemoryRouter { FailReadPath = "/source/folder/bad.bin" };
        router.Directory("/source/folder");
        router.File("/source/folder/bad.bin", "bad");
        var service = new RemoteCopyService(router);

        var act = () => service.CopyAsync(Request("/source/folder", "/target/folder"),
            Context, Context, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        router.Exists("/target/folder").Should().BeFalse();
        router.Paths.Should().NotContain(path => path.Contains(".watashi-copy-", StringComparison.Ordinal));
    }

    private static RemoteCopyRequest Request(string source, string target, string collision = "fail") => new()
    {
        SourceHostId = 1,
        SourceShareId = 1,
        SourcePath = source,
        TargetHostId = 1,
        TargetShareId = 1,
        TargetPath = target,
        CollisionPolicy = collision,
    };

    private sealed class MemoryRouter : NodeRouter
    {
        private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
        public string? FailReadPath { get; init; }
        public IReadOnlyCollection<string> Paths => _items.Keys;

        public MemoryRouter() : base(null!, null!) { }

        public void Directory(string path) => _items[Normalize(path)] = new(true, Array.Empty<byte>());
        public void File(string path, string content) =>
            _items[Normalize(path)] = new(false, System.Text.Encoding.UTF8.GetBytes(content));
        public bool Exists(string path) => _items.ContainsKey(Normalize(path));
        public string Text(string path) => System.Text.Encoding.UTF8.GetString(_items[Normalize(path)].Content);

        public override Task<TransferFileMetadata> GetTransferMetadataAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            if (!_items.TryGetValue(Normalize(path), out var item))
                return Task.FromResult(TransferFileMetadata.Missing);
            return Task.FromResult(new TransferFileMetadata(true,
                item.Directory ? TransferFileTypes.Directory : TransferFileTypes.File,
                item.Directory ? null : item.Content.LongLength,
                DateTime.UtcNow,
                false));
        }

        public override Task<IReadOnlyList<FileEntry>> ListAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            var parent = Normalize(path);
            var prefix = parent == "/" ? "/" : parent + "/";
            var entries = _items
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(pair => (pair.Key, pair.Value, Relative: pair.Key[prefix.Length..]))
                .Where(x => x.Relative.Length > 0 && !x.Relative.Contains('/'))
                .Select(x => new FileEntry
                {
                    Name = x.Relative,
                    Type = x.Value.Directory ? FileEntryTypes.Directory : FileEntryTypes.File,
                    Size = x.Value.Directory ? null : x.Value.Content.LongLength,
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<FileEntry>>(entries);
        }

        public override Task<Stream> OpenReadAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            var normalized = Normalize(path);
            if (string.Equals(normalized, FailReadPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("simulated read failure");
            return Task.FromResult<Stream>(new MemoryStream(_items[normalized].Content, writable: false));
        }

        public override async Task UploadAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, Stream input, CancellationToken ct)
        {
            await using var output = new MemoryStream();
            await input.CopyToAsync(output, ct);
            _items[Normalize(path)] = new(false, output.ToArray());
        }

        public override Task MkdirAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            var normalized = Normalize(path);
            if (_items.ContainsKey(normalized)) throw new IOException("already exists");
            _items[normalized] = new(true, Array.Empty<byte>());
            return Task.CompletedTask;
        }

        public override Task RenameAsync(
            ExecutionNode node, CifsConnectionInfo info, string oldPath, string newPath, CancellationToken ct)
        {
            var source = Normalize(oldPath);
            var target = Normalize(newPath);
            if (_items.ContainsKey(target)) throw new IOException("already exists");
            var affected = _items.Where(x => x.Key.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                             x.Key.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var pair in affected) _items.Remove(pair.Key);
            foreach (var pair in affected)
                _items[target + pair.Key[source.Length..]] = pair.Value;
            return Task.CompletedTask;
        }

        public override Task DeleteAsync(
            ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
        {
            var target = Normalize(path);
            foreach (var key in _items.Keys.Where(key =>
                         key.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
                _items.Remove(key);
            return Task.CompletedTask;
        }

        private static string Normalize(string path) => PathHelper.NormalizePath(path);
        private sealed record Item(bool Directory, byte[] Content);
    }
}
