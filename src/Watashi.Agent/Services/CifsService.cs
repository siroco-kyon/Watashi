using SMBLibrary;
using Watashi.Agent.Services.Cifs;
using Watashi.Shared.DTOs.Files;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Watashi.Agent.Services;

public class CifsService
{
    public IReadOnlyList<FileEntry> List(CifsConnectionInfo info, string path)
    {
        using var session = CifsSession.Connect(info);
        var smbPath = ToSmbDirectory(path);
        var status = session.Store.CreateFile(
            out object dirHandle, out FileStatus _, smbPath,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"ディレクトリを開けません ({smbPath}): {status}");
        try
        {
            var queryStatus = session.Store.QueryDirectory(out var entries, dirHandle, "*", FileInformationClass.FileDirectoryInformation);
            if (queryStatus != NTStatus.STATUS_SUCCESS && queryStatus != NTStatus.STATUS_NO_MORE_FILES)
                throw new IOException($"ディレクトリ列挙失敗: {queryStatus}");
            var list = new List<FileEntry>();
            foreach (var item in entries.OfType<FileDirectoryInformation>())
            {
                if (item.FileName is "." or "..") continue;
                bool isDir = (item.FileAttributes & FileAttributes.Directory) != 0;
                list.Add(new FileEntry
                {
                    Name = item.FileName,
                    Type = isDir ? "directory" : "file",
                    Size = isDir ? null : item.EndOfFile,
                    ModifiedAt = DateTime.SpecifyKind(item.LastWriteTime, DateTimeKind.Utc),
                });
            }
            return list;
        }
        finally { session.Store.CloseFile(dirHandle); }
    }

    public Stream OpenRead(CifsConnectionInfo info, string path)
    {
        var session = CifsSession.Connect(info);
        try
        {
            var smbPath = ToSmbFile(path);
            var status = session.Store.CreateFile(
                out object handle, out FileStatus _, smbPath,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"ファイルを開けません ({smbPath}): {status}");
            var infoStatus = session.Store.GetFileInformation(out FileInformation infoObj, handle, FileInformationClass.FileStandardInformation);
            long size = 0;
            if (infoStatus == NTStatus.STATUS_SUCCESS && infoObj is FileStandardInformation std)
                size = std.EndOfFile;
            return new SmbReadStream(session, handle, size);
        }
        catch { session.Dispose(); throw; }
    }

    public Stream OpenWrite(CifsConnectionInfo info, string path)
    {
        var session = CifsSession.Connect(info);
        try
        {
            var smbPath = ToSmbFile(path);
            var status = session.Store.CreateFile(
                out object handle, out FileStatus _, smbPath,
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.None,
                CreateDisposition.FILE_OVERWRITE_IF,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"ファイル書き込みオープン失敗 ({smbPath}): {status}");
            return new SmbWriteStream(session, handle);
        }
        catch { session.Dispose(); throw; }
    }

    public void Delete(CifsConnectionInfo info, string path)
    {
        using var session = CifsSession.Connect(info);
        var smbPath = ToSmbFile(path);
        var status = session.Store.CreateFile(
            out object handle, out FileStatus _, smbPath,
            AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS) throw new IOException($"削除失敗 ({smbPath}): {status}");
        session.Store.CloseFile(handle);
    }

    public void Rename(CifsConnectionInfo info, string oldPath, string newPath)
    {
        using var session = CifsSession.Connect(info);
        var smbOld = ToSmbFile(oldPath);
        var smbNew = ToSmbFile(newPath);
        var status = session.Store.CreateFile(
            out object handle, out FileStatus _, smbOld,
            AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS) throw new IOException($"リネーム対象を開けません ({smbOld}): {status}");
        try
        {
            var rename = new FileRenameInformationType2 { ReplaceIfExists = false, FileName = smbNew };
            var setStatus = session.Store.SetFileInformation(handle, rename);
            if (setStatus != NTStatus.STATUS_SUCCESS) throw new IOException($"リネーム失敗: {setStatus}");
        }
        finally { session.Store.CloseFile(handle); }
    }

    public void Mkdir(CifsConnectionInfo info, string path)
    {
        using var session = CifsSession.Connect(info);
        var smbPath = ToSmbDirectory(path);
        var status = session.Store.CreateFile(
            out object handle, out FileStatus _, smbPath,
            AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Directory, ShareAccess.None,
            CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
        if (status != NTStatus.STATUS_SUCCESS) throw new IOException($"フォルダ作成失敗 ({smbPath}): {status}");
        session.Store.CloseFile(handle);
    }

    public bool TestConnection(CifsConnectionInfo info)
    {
        try { using var _ = CifsSession.Connect(info); return true; } catch { return false; }
    }

    private static string ToSmbDirectory(string path)
    {
        var p = (path ?? "/").Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(p) ? string.Empty : p.Replace('/', '\\');
    }

    private static string ToSmbFile(string path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return p.Replace('/', '\\');
    }
}
