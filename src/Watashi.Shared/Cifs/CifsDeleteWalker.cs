using SMBLibrary;

namespace Watashi.Shared.Cifs;

internal interface ICifsDeleteOperations
{
    NTStatus TryDeleteFile(string path);
    IReadOnlyList<CifsDeleteEntry> ListDirectory(string path);
    NTStatus TryDeleteEmptyDirectory(string path);
}

internal readonly record struct CifsDeleteEntry(string Name, bool IsDirectory);

internal static class CifsDeleteWalker
{
    public static void Delete(string path, ICifsDeleteOperations operations)
    {
        var status = operations.TryDeleteFile(path);
        if (status == NTStatus.STATUS_SUCCESS) return;

        if (status == NTStatus.STATUS_FILE_IS_A_DIRECTORY)
        {
            DeleteDirectoryRecursive(path, operations);
            return;
        }

        throw new IOException($"削除エラー: {status}");
    }

    private static void DeleteDirectoryRecursive(string path, ICifsDeleteOperations operations)
    {
        foreach (var item in operations.ListDirectory(path))
        {
            var childPath = JoinPath(path, item.Name);
            if (item.IsDirectory)
            {
                DeleteDirectoryRecursive(childPath, operations);
                continue;
            }

            var fileStatus = operations.TryDeleteFile(childPath);
            if (fileStatus != NTStatus.STATUS_SUCCESS)
                throw new IOException($"ファイル削除エラー ({childPath}): {fileStatus}");
        }

        var dirStatus = operations.TryDeleteEmptyDirectory(path);
        if (dirStatus != NTStatus.STATUS_SUCCESS)
            throw new IOException($"フォルダ削除エラー ({path}): {dirStatus}");
    }

    private static string JoinPath(string parent, string name)
    {
        var p = (parent ?? "/").Replace('\\', '/').TrimEnd('/');
        return string.IsNullOrEmpty(p) ? "/" + name : p + "/" + name;
    }
}
