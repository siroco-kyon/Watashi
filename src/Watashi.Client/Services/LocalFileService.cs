using System.IO;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

public class LocalFileService
{
    public IReadOnlyList<FileEntry> List(string path)
    {
        var list = new List<FileEntry>();
        var di = new DirectoryInfo(path);
        if (!di.Exists) return list;
        foreach (var d in di.GetDirectories())
        {
            list.Add(new FileEntry { Name = d.Name, Type = "directory", ModifiedAt = d.LastWriteTimeUtc });
        }
        foreach (var f in di.GetFiles())
        {
            list.Add(new FileEntry { Name = f.Name, Type = "file", Size = f.Length, ModifiedAt = f.LastWriteTimeUtc });
        }
        return list;
    }

    public IReadOnlyList<string> Drives() => DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
}
