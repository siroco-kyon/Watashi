using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Tests;

/// <summary>
/// 競合方針ダイアログを出すかどうかの判定。同名が無いのに「上書きしますか？」と聞かれないこと、
/// および同名を見落とさないことを担保する。
/// </summary>
public class TransferConflictPlannerTests
{
    [Fact]
    public async Task No_conflict_when_destination_has_no_matching_name()
    {
        var remote = new FakeRemote { ["/dst"] = new[] { File("other.txt") } };

        var result = await remote.HasConflictAsync("/dst", "/dst/a.txt", "/dst/b.txt");

        result.Should().BeFalse();
        remote.ListedPaths.Should().Equal("/dst");
    }

    [Fact]
    public async Task Conflict_when_same_file_name_exists()
    {
        var remote = new FakeRemote { ["/dst"] = new[] { File("a.txt") } };

        var result = await remote.HasConflictAsync("/dst", "/dst/a.txt");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Name_comparison_ignores_case()
    {
        var remote = new FakeRemote { ["/dst"] = new[] { File("A.TXT") } };

        (await remote.HasConflictAsync("/dst", "/dst/a.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Existing_directory_with_same_name_as_planned_file_is_a_conflict()
    {
        var remote = new FakeRemote { ["/dst"] = new[] { Dir("report") } };

        (await remote.HasConflictAsync("/dst", "/dst/report")).Should().BeTrue();
    }

    [Fact]
    public async Task New_folder_subtree_is_never_listed()
    {
        // /dst に "案件A" が無い = その配下はすべて新規。深い階層を問い合わせてはいけない。
        var remote = new FakeRemote { ["/dst"] = new[] { File("readme.txt") } };

        var result = await remote.HasConflictAsync(
            "/dst", "/dst/案件A/doc/a.txt", "/dst/案件A/doc/sub/b.txt");

        result.Should().BeFalse();
        remote.ListedPaths.Should().Equal("/dst");
    }

    [Fact]
    public async Task Existing_folder_is_merged_and_its_children_are_checked()
    {
        var remote = new FakeRemote
        {
            ["/dst"] = new[] { Dir("案件A") },
            ["/dst/案件A"] = new[] { Dir("doc") },
            ["/dst/案件A/doc"] = new[] { File("a.txt") },
        };

        var result = await remote.HasConflictAsync("/dst", "/dst/案件A/doc/a.txt");

        result.Should().BeTrue();
        remote.ListedPaths.Should().Equal("/dst", "/dst/案件A", "/dst/案件A/doc");
    }

    [Fact]
    public async Task Merged_folder_without_matching_files_is_not_a_conflict()
    {
        var remote = new FakeRemote
        {
            ["/dst"] = new[] { Dir("案件A") },
            ["/dst/案件A"] = new[] { File("old.txt") },
        };

        (await remote.HasConflictAsync("/dst", "/dst/案件A/new.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Parent_entries_are_ignored()
    {
        var remote = new FakeRemote
        {
            ["/dst"] = new[] { new FileEntry { Name = "a.txt", Type = FileEntryTypes.Parent } },
        };

        (await remote.HasConflictAsync("/dst", "/dst/a.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Empty_plan_does_not_query_the_server()
    {
        var remote = new FakeRemote { ["/dst"] = new[] { File("a.txt") } };

        (await remote.HasConflictAsync("/dst")).Should().BeFalse();
        remote.ListedPaths.Should().BeEmpty();
    }

    [Fact]
    public void Local_conflict_is_detected_for_existing_file_or_directory()
    {
        using var temp = new TemporaryDirectory();
        var existingFile = Path.Combine(temp.Path, "a.txt");
        System.IO.File.WriteAllText(existingFile, "x");
        var existingDir = Path.Combine(temp.Path, "sub");
        Directory.CreateDirectory(existingDir);

        TransferConflictPlanner.HasLocalConflict(new[] { Path.Combine(temp.Path, "b.txt") }).Should().BeFalse();
        TransferConflictPlanner.HasLocalConflict(new[] { Path.Combine(temp.Path, "b.txt"), existingFile }).Should().BeTrue();
        TransferConflictPlanner.HasLocalConflict(new[] { existingDir }).Should().BeTrue();
    }

    private static FileEntry File(string name) => new() { Name = name, Type = FileEntryTypes.File };
    private static FileEntry Dir(string name) => new() { Name = name, Type = FileEntryTypes.Directory };

    /// <summary>リモート一覧を差し替え、どのフォルダを何回問い合わせたかを記録する。</summary>
    private sealed class FakeRemote : Dictionary<string, IReadOnlyList<FileEntry>>
    {
        public FakeRemote() : base(StringComparer.OrdinalIgnoreCase) { }

        public List<string> ListedPaths { get; } = new();

        public Task<bool> HasConflictAsync(string baseDir, params string[] plannedRemotePaths)
            => TransferConflictPlanner.HasRemoteConflictAsync(baseDir, plannedRemotePaths, ListAsync);

        private Task<List<FileEntry>> ListAsync(string path)
        {
            ListedPaths.Add(path);
            return Task.FromResult(TryGetValue(path, out var entries) ? entries.ToList() : new List<FileEntry>());
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "watashi-conflict-planner-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
