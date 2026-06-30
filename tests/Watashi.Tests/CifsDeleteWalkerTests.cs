using FluentAssertions;
using SMBLibrary;
using Watashi.Shared.Cifs;
using Xunit;

namespace Watashi.Tests;

public class CifsDeleteWalkerTests
{
    [Fact]
    public void Delete_file_deletes_without_listing_directory()
    {
        var ops = new FakeDeleteOperations();

        CifsDeleteWalker.Delete("/file.txt", ops);

        ops.Calls.Should().Equal("file:/file.txt");
    }

    [Fact]
    public void Delete_empty_directory_deletes_directory_after_listing()
    {
        var ops = new FakeDeleteOperations();
        ops.Directories["/empty"] = new();

        CifsDeleteWalker.Delete("/empty", ops);

        ops.Calls.Should().Equal(
            "file:/empty",
            "list:/empty",
            "dir:/empty");
    }

    [Fact]
    public void Delete_directory_deletes_children_before_parent()
    {
        var ops = new FakeDeleteOperations();
        ops.Directories["/root"] = new()
        {
            new("sub", true),
            new("a.txt", false),
        };
        ops.Directories["/root/sub"] = new()
        {
            new("b.txt", false),
        };

        CifsDeleteWalker.Delete("/root", ops);

        ops.Calls.Should().Equal(
            "file:/root",
            "list:/root",
            "list:/root/sub",
            "file:/root/sub/b.txt",
            "dir:/root/sub",
            "file:/root/a.txt",
            "dir:/root");
    }

    [Fact]
    public void Delete_directory_joins_root_children_correctly()
    {
        var ops = new FakeDeleteOperations();
        ops.Directories["/"] = new()
        {
            new("child.txt", false),
        };

        CifsDeleteWalker.Delete("/", ops);

        ops.Calls.Should().Equal(
            "file:/",
            "list:/",
            "file:/child.txt",
            "dir:/");
    }

    [Fact]
    public void Delete_directory_stops_before_parent_when_child_file_delete_fails()
    {
        var ops = new FakeDeleteOperations();
        ops.Directories["/root"] = new()
        {
            new("locked.txt", false),
        };
        ops.FileStatuses["/root/locked.txt"] = NTStatus.STATUS_ACCESS_DENIED;

        var act = () => CifsDeleteWalker.Delete("/root", ops);

        act.Should().Throw<IOException>()
            .WithMessage("*locked.txt*STATUS_ACCESS_DENIED*");
        ops.Calls.Should().Equal(
            "file:/root",
            "list:/root",
            "file:/root/locked.txt");
    }

    [Fact]
    public void Delete_directory_reports_parent_delete_failure_after_children()
    {
        var ops = new FakeDeleteOperations();
        ops.Directories["/root"] = new()
        {
            new("child.txt", false),
        };
        ops.DirectoryStatuses["/root"] = NTStatus.STATUS_DIRECTORY_NOT_EMPTY;

        var act = () => CifsDeleteWalker.Delete("/root", ops);

        act.Should().Throw<IOException>()
            .WithMessage("*root*STATUS_DIRECTORY_NOT_EMPTY*");
        ops.Calls.Should().Equal(
            "file:/root",
            "list:/root",
            "file:/root/child.txt",
            "dir:/root");
    }

    private sealed class FakeDeleteOperations : ICifsDeleteOperations
    {
        public Dictionary<string, List<CifsDeleteEntry>> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, NTStatus> FileStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, NTStatus> DirectoryStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = new();

        public NTStatus TryDeleteFile(string path)
        {
            Calls.Add("file:" + path);
            if (Directories.ContainsKey(path))
                return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
            return FileStatuses.TryGetValue(path, out var status)
                ? status
                : NTStatus.STATUS_SUCCESS;
        }

        public IReadOnlyList<CifsDeleteEntry> ListDirectory(string path)
        {
            Calls.Add("list:" + path);
            return Directories.TryGetValue(path, out var entries)
                ? entries
                : Array.Empty<CifsDeleteEntry>();
        }

        public NTStatus TryDeleteEmptyDirectory(string path)
        {
            Calls.Add("dir:" + path);
            return DirectoryStatuses.TryGetValue(path, out var status)
                ? status
                : NTStatus.STATUS_SUCCESS;
        }
    }
}
