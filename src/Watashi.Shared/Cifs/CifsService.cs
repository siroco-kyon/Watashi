using SMBLibrary;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Watashi.Shared.Cifs;

/// <summary>
/// SMBLibrary を使った CIFS 操作。CifsSessionPool 経由で接続を再利用する。
/// </summary>
public class CifsService
{
    private readonly CifsSessionPool _pool;

    public CifsService(CifsSessionPool pool)
    {
        _pool = pool;
    }

    private CifsSession Acquire(CifsConnectionInfo info) => _pool.Acquire(info);

    public IReadOnlyList<FileEntry> List(CifsConnectionInfo info, string path)
    {
        using var session = Acquire(info);
        var smbPath = ToSmbDirectory(path);
        var status = session.Store.CreateFile(
            out object dirHandle, out FileStatus _, smbPath,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
            null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"ディレクトリを開けません: {status}");
        try
        {
            var queryStatus = session.Store.QueryDirectory(out var entries, dirHandle, "*", FileInformationClass.FileDirectoryInformation);
            if (queryStatus != NTStatus.STATUS_SUCCESS && queryStatus != NTStatus.STATUS_NO_MORE_FILES)
                throw new IOException($"ディレクトリ列挙エラー: {queryStatus}");

            var list = new List<FileEntry>();
            foreach (var item in entries.OfType<FileDirectoryInformation>())
            {
                if (item.FileName is "." or "..") continue;
                bool isDir = (item.FileAttributes & FileAttributes.Directory) != 0;
                list.Add(new FileEntry
                {
                    Name = item.FileName,
                    Type = isDir ? FileEntryTypes.Directory : FileEntryTypes.File,
                    Size = isDir ? null : item.EndOfFile,
                    ModifiedAt = DateTime.SpecifyKind(item.LastWriteTime, DateTimeKind.Utc),
                });
            }
            return list;
        }
        finally
        {
            try { session.Store.CloseFile(dirHandle); } catch { }
        }
    }

    public Stream OpenRead(CifsConnectionInfo info, string path)
    {
        var session = Acquire(info);
        try
        {
            var smbPath = ToSmbFile(path);
            var status = session.Store.CreateFile(
                out object handle, out FileStatus _, smbPath,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                FileAttributes.Normal,
                ShareAccess.Read,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"ファイルを開けません: {status}");

            // サイズ取得に失敗したまま size=0 で続行すると、SmbReadStream が即 EOF を返し
            // 「0 バイトのダウンロード成功」として既存ファイルを空で上書きしてしまう。必ず失敗させる。
            var infoStatus = session.Store.GetFileInformation(out FileInformation infoObj, handle, FileInformationClass.FileStandardInformation);
            if (infoStatus != NTStatus.STATUS_SUCCESS || infoObj is not FileStandardInformation std)
            {
                try { session.Store.CloseFile(handle); } catch { }
                throw new IOException($"ファイルサイズ取得エラー: {infoStatus}");
            }

            return new SmbReadStream(session, handle, std.EndOfFile);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Stream OpenWrite(CifsConnectionInfo info, string path)
    {
        var session = Acquire(info);
        try
        {
            var smbPath = ToSmbFile(path);
            var status = session.Store.CreateFile(
                out object handle, out FileStatus _, smbPath,
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                FileAttributes.Normal,
                ShareAccess.None,
                CreateDisposition.FILE_OVERWRITE_IF,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"ファイル書き込みオープンエラー: {status}");
            return new SmbWriteStream(session, handle);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Delete(CifsConnectionInfo info, string path)
    {
        using var session = Acquire(info);
        CifsDeleteWalker.Delete(path, new SmbDeleteOperations(session));
    }

    private sealed class SmbDeleteOperations : ICifsDeleteOperations
    {
        private readonly CifsSession _session;

        public SmbDeleteOperations(CifsSession session)
        {
            _session = session;
        }

        public NTStatus TryDeleteFile(string path)
        {
            var smbPath = ToSmbFile(path);
            var status = _session.Store.CreateFile(
                out object handle, out FileStatus _, smbPath,
                AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            return status == NTStatus.STATUS_SUCCESS ? _session.Store.CloseFile(handle) : status;
        }

        public NTStatus TryDeleteEmptyDirectory(string path)
        {
            var smbPath = ToSmbDirectory(path);
            var status = _session.Store.CreateFile(
                out object handle, out FileStatus _, smbPath,
                AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            return status == NTStatus.STATUS_SUCCESS ? _session.Store.CloseFile(handle) : status;
        }

        public IReadOnlyList<CifsDeleteEntry> ListDirectory(string path)
        {
            var smbPath = ToSmbDirectory(path);
            var status = _session.Store.CreateFile(
                out object dirHandle, out FileStatus _, smbPath,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"ディレクトリを開けません: {status}");

            try
            {
                var queryStatus = _session.Store.QueryDirectory(out var entries, dirHandle, "*", FileInformationClass.FileDirectoryInformation);
                if (queryStatus != NTStatus.STATUS_SUCCESS && queryStatus != NTStatus.STATUS_NO_MORE_FILES)
                    throw new IOException($"ディレクトリ列挙エラー: {queryStatus}");

                return (entries ?? new List<QueryDirectoryFileInformation>())
                    .OfType<FileDirectoryInformation>()
                    .Where(item => item.FileName is not ("." or ".."))
                    .Select(item => new CifsDeleteEntry(
                        item.FileName,
                        (item.FileAttributes & FileAttributes.Directory) != 0))
                    .ToList();
            }
            finally
            {
                try { _session.Store.CloseFile(dirHandle); } catch { }
            }
        }
    }

    public void Rename(CifsConnectionInfo info, string oldPath, string newPath)
    {
        using var session = Acquire(info);
        var smbOld = ToSmbFile(oldPath);
        var smbNew = ToSmbFile(newPath);
        var status = session.Store.CreateFile(
            out object handle, out FileStatus _, smbOld,
            AccessMask.DELETE | AccessMask.SYNCHRONIZE,
            FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
            null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"リネーム対象を開けません: {status}");
        try
        {
            var rename = new FileRenameInformationType2 { ReplaceIfExists = false, FileName = smbNew };
            var setStatus = session.Store.SetFileInformation(handle, rename);
            if (setStatus != NTStatus.STATUS_SUCCESS)
                throw new IOException($"リネームエラー: {setStatus}");
        }
        finally
        {
            try { session.Store.CloseFile(handle); } catch { }
        }
    }

    public void Mkdir(CifsConnectionInfo info, string path)
    {
        using var session = Acquire(info);
        var smbPath = ToSmbDirectory(path);
        var status = session.Store.CreateFile(
            out object handle, out FileStatus _, smbPath,
            AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
            FileAttributes.Directory,
            ShareAccess.None,
            CreateDisposition.FILE_CREATE,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
            null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"フォルダ作成エラー: {status}");
        try { session.Store.CloseFile(handle); } catch { }
    }

    public bool TestConnection(CifsConnectionInfo info)
    {
        try
        {
            using var _ = Acquire(info);
            return true;
        }
        catch
        {
            return false;
        }
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
