using System.IO;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

public class LocalFileService
{
    /// <summary>
    /// EnumerateDirectories/EnumerateFiles を使い、大量ファイルがある場合でも 1 回の OS コールで全件展開しない。
    /// 同期 I/O なので Task.Run などで呼ぶこと。
    /// </summary>
    public IReadOnlyList<FileEntry> List(string path)
    {
        var list = new List<FileEntry>();
        var di = new DirectoryInfo(path);
        if (!di.Exists) return list;
        foreach (var d in di.EnumerateDirectories())
        {
            list.Add(new FileEntry { Name = d.Name, Type = FileEntryTypes.Directory, ModifiedAt = d.LastWriteTimeUtc });
        }
        foreach (var f in di.EnumerateFiles())
        {
            list.Add(new FileEntry { Name = f.Name, Type = FileEntryTypes.File, Size = f.Length, ModifiedAt = f.LastWriteTimeUtc });
        }
        return list;
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct = default)
        => Task.Run(() => List(path), ct);

    public IReadOnlyList<string> Drives() => DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
}
