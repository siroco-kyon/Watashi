using System.IO;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Client.Services;

/// <summary>
/// 転送キューへ追加する前に「転送先に同名項目があるか」を判定する。
/// 競合が起こり得ないと分かっている場合は競合方針ダイアログ (TransferConflictDialog) を省略でき、
/// 「上書きしますか？」と聞かれる場面を実際に競合しているときだけに絞れる。
/// WPF へ依存しないため、そのまま回帰テストできる。
/// </summary>
public static class TransferConflictPlanner
{
    /// <summary>
    /// アップロード予定のリモートパス群に、既にリモート側の項目があるかを調べる。
    /// <paramref name="baseRemoteDir"/> は実在するフォルダ (通常は表示中のリモートフォルダ) を渡すこと。
    /// 一覧するのは実在が確認できたフォルダだけで、新規フォルダのサブツリーには一切問い合わせない
    /// (親の一覧に無いフォルダは、その配下すべてが新規であることが確定するため)。
    /// </summary>
    public static async Task<bool> HasRemoteConflictAsync(
        string baseRemoteDir,
        IEnumerable<string> plannedRemotePaths,
        Func<string, Task<List<FileEntry>>> listAsync)
    {
        var basePath = PathHelper.NormalizePath(baseRemoteDir);
        // フォルダ -> そこへ新規作成する項目名 (これが既存項目とぶつかれば競合)
        var plannedNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // フォルダ -> たどる必要のある子フォルダ名 (既存なら結合となり、その中も調べる)
        var plannedDirs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in plannedRemotePaths)
        {
            var normalized = PathHelper.NormalizePath(path);
            if (string.Equals(normalized, basePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!PathHelper.IsPathWithin(basePath, normalized)) continue;

            Register(plannedNames, PathHelper.GetParent(normalized), NameOf(normalized));
            for (var dir = PathHelper.GetParent(normalized);
                 dir != "/" && !string.Equals(dir, basePath, StringComparison.OrdinalIgnoreCase);
                 dir = PathHelper.GetParent(dir))
            {
                Register(plannedDirs, PathHelper.GetParent(dir), NameOf(dir));
            }
        }
        if (plannedNames.Count == 0) return false;

        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { basePath };
        pending.Enqueue(basePath);
        while (pending.Count > 0)
        {
            var dir = pending.Dequeue();
            plannedNames.TryGetValue(dir, out var names);
            plannedDirs.TryGetValue(dir, out var subdirs);
            foreach (var entry in await listAsync(dir))
            {
                if (entry.Type == FileEntryTypes.Parent) continue;
                if (names?.Contains(entry.Name) == true) return true;
                if (entry.Type != FileEntryTypes.Directory || subdirs?.Contains(entry.Name) != true) continue;

                var child = Join(dir, entry.Name);
                if (visited.Add(child)) pending.Enqueue(child);
            }
        }
        return false;
    }

    /// <summary>ダウンロード予定のローカルパス群に、既にファイル/フォルダがあるか。</summary>
    public static bool HasLocalConflict(IEnumerable<string> plannedLocalPaths)
        => plannedLocalPaths.Any(p => File.Exists(p) || Directory.Exists(p));

    private static void Register(Dictionary<string, HashSet<string>> map, string dir, string name)
    {
        if (!map.TryGetValue(dir, out var names))
            map[dir] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.Add(name);
    }

    private static string NameOf(string normalizedPath)
        => normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];

    private static string Join(string parent, string name)
        => parent == "/" ? "/" + name : parent + "/" + name;
}
