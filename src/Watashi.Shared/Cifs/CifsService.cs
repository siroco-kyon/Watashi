using SMBLibrary;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;
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

    private CifsSession Acquire(CifsConnectionInfo info, CancellationToken ct = default) => _pool.Acquire(info, ct);

    public IReadOnlyList<FileEntry> List(CifsConnectionInfo info, string path, CancellationToken ct = default)
    {
        using var session = Acquire(info, ct);
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
                bool isReparsePoint = (item.FileAttributes & FileAttributes.ReparsePoint) != 0;
                list.Add(new FileEntry
                {
                    Name = item.FileName,
                    Type = isDir ? FileEntryTypes.Directory : FileEntryTypes.File,
                    Size = isDir ? null : item.EndOfFile,
                    ModifiedAt = DateTime.SpecifyKind(item.LastWriteTime, DateTimeKind.Utc),
                    IsReparsePoint = isReparsePoint,
                });
            }
            return list;
        }
        finally
        {
            try { session.Store.CloseFile(dirHandle); } catch { }
        }
    }

    public Stream OpenRead(CifsConnectionInfo info, string path, CancellationToken ct = default)
    {
        var session = Acquire(info, ct);
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

    /// <summary>
    /// Transfer v2 用 metadata。FILE_OPEN_REPARSE_POINT で対象自身を調べるため、
    /// リンク先へは移動しない。存在しない場合だけ Exists=false を返す。
    /// </summary>
    public virtual TransferFileMetadata GetTransferMetadata(
        CifsConnectionInfo info,
        string path,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var session = Acquire(info, ct);
        var status = OpenTransferHandle(
            session,
            path,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN,
            out var handle);
        if (IsMissing(status)) return TransferFileMetadata.Missing;
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"metadata 取得用に対象を開けません: {status}");
        try
        {
            return ReadTransferMetadata(session, handle, path, ct);
        }
        finally
        {
            CloseTransferHandle(session, handle);
        }
    }

    /// <summary>
    /// ごみ箱へ移動する対象の種別と総サイズを調べる。各パス要素と配下の項目を
    /// FILE_OPEN_REPARSE_POINT / directory attributes で検査し、リンクは辿らず拒否する。
    /// </summary>
    public virtual RemoteTrashItemMetadata InspectForTrash(
        CifsConnectionInfo info,
        string path,
        CancellationToken ct = default)
    {
        var normalized = RemoteTrashPathPolicy.NormalizeUserPath(path);
        EnsureNoReparseAncestors(info, normalized, includeLeaf: true, ct);
        var metadata = GetTransferMetadata(info, normalized, ct);
        if (!metadata.Exists)
            throw new FileNotFoundException("ごみ箱へ移動する対象が見つかりません。", normalized);
        if (metadata.IsReparsePoint)
            throw new TransferReparsePointException(normalized);

        var size = metadata.Type == TransferFileTypes.Directory
            ? MeasureDirectoryTree(info, normalized, ct)
            : metadata.Size ?? throw new IOException($"ファイルサイズを取得できません: {normalized}");
        return new RemoteTrashItemMetadata(metadata.Type, size, metadata.ModifiedAtUtc);
    }

    /// <summary>同じ共有の管理用隔離領域へ SMB rename で原子的に移動する。</summary>
    public virtual void MoveToTrash(
        CifsConnectionInfo info,
        string sourcePath,
        string trashPath,
        CancellationToken ct = default)
    {
        var source = RemoteTrashPathPolicy.NormalizeUserPath(sourcePath);
        var target = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        EnsureNoReparseAncestors(info, source, includeLeaf: true, ct);
        EnsureManagedTrashRoot(info, ct);
        AtomicRenameManaged(info, source, target, replaceIfExists: false, ct);
    }

    /// <summary>管理用隔離領域から通常パスへ SMB rename で原子的に復元する。</summary>
    public virtual void RestoreFromTrash(
        CifsConnectionInfo info,
        string trashPath,
        string targetPath,
        bool replaceIfExists,
        CancellationToken ct = default)
    {
        var source = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        var target = RemoteTrashPathPolicy.NormalizeUserPath(targetPath);
        EnsureManagedTrashRoot(info, ct);
        EnsureNoReparseAncestors(info, source, includeLeaf: true, ct);
        EnsureNoReparseAncestors(info, PathHelper.GetParent(target), includeLeaf: true, ct);

        var existing = GetTransferMetadata(info, target, ct);
        if (existing.Exists && existing.IsReparsePoint)
            throw new TransferReparsePointException(target);
        AtomicRenameManaged(info, source, target, replaceIfExists, ct);
    }

    /// <summary>
    /// 管理用隔離領域の1項目だけを完全削除する。再帰削除は既存walkerが
    /// リパースポイントをリンク自身として削除し、リンク先へは入らない。
    /// </summary>
    public virtual void PurgeTrashItem(
        CifsConnectionInfo info,
        string trashPath,
        CancellationToken ct = default)
    {
        var normalized = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        EnsureManagedTrashRoot(info, ct);
        var metadata = GetTransferMetadata(info, normalized, ct);
        if (!metadata.Exists) return;
        if (metadata.IsReparsePoint)
            throw new TransferReparsePointException(normalized);
        Delete(info, normalized, ct);
    }

    /// <summary>
    /// Transfer v2 session 用の空の一時ファイルを同名再送に安全な形で確保する。
    /// 既に存在する場合は切り詰めず、その metadata を返す。
    /// </summary>
    public virtual TransferFileMetadata EnsureTempFile(
        CifsConnectionInfo info,
        string tempPath,
        CancellationToken ct = default)
    {
        var normalizedPath = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
        ct.ThrowIfCancellationRequested();
        using var session = Acquire(info, ct);
        var status = OpenTransferHandle(
            session,
            normalizedPath,
            AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
            ShareAccess.None,
            CreateDisposition.FILE_OPEN_IF,
            out var handle,
            requireFile: true);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"一時ファイルを作成できません: {status}");
        try
        {
            var metadata = ReadTransferMetadata(session, handle, normalizedPath, ct);
            TransferV2Validation.EnsureRegularFile(metadata, normalizedPath);
            return metadata;
        }
        finally
        {
            CloseTransferHandle(session, handle);
        }
    }

    /// <summary>Transfer v2 用の offset/length 指定読み取り。length は最大 8 MiB。</summary>
    public virtual Stream OpenReadRange(
        CifsConnectionInfo info,
        string path,
        long offset,
        int length,
        CancellationToken ct = default)
    {
        TransferV2Validation.ValidateReadRange(offset, length);
        return OpenVerifiedRead(info, path, offset, length, ct);
    }

    /// <summary>
    /// Watashi 一時ファイルへ chunk を整合性付きで書く。同一 chunk の再送は no-op、
    /// 途中まで書かれた chunk の再送は既存 prefix を照合して残りだけ書き足す。
    /// </summary>
    public virtual TransferChunkWriteResult WriteTempChunk(
        CifsConnectionInfo info,
        string tempPath,
        long offset,
        ReadOnlyMemory<byte> chunk,
        CancellationToken ct = default)
    {
        var normalizedPath = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
        TransferV2Validation.ValidateChunk(offset, chunk.Length);
        ct.ThrowIfCancellationRequested();

        using var session = Acquire(info, ct);
        var disposition = offset == 0 ? CreateDisposition.FILE_OPEN_IF : CreateDisposition.FILE_OPEN;
        var status = OpenTransferHandle(
            session,
            normalizedPath,
            AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
            ShareAccess.None,
            disposition,
            out var handle,
            requireFile: true);
        if (IsMissing(status) && offset > 0)
            throw new TransferOffsetMismatchException(offset, 0, "一時ファイルがまだ存在しません。");
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"一時ファイルを開けません: {status}");

        try
        {
            var metadata = ReadTransferMetadata(session, handle, normalizedPath, ct);
            TransferV2Validation.EnsureRegularFile(metadata, normalizedPath);
            var currentLength = metadata.Size ?? 0;
            if (currentLength < offset)
                throw new TransferOffsetMismatchException(offset, currentLength, "未書き込みの空白があります。");

            var overlap = (int)Math.Min(currentLength - offset, chunk.Length);
            var existing = overlap == 0
                ? Array.Empty<byte>()
                : ReadExact(session, handle, offset, overlap, ct);
            var resumeAt = TransferV2Validation.ReconcileChunk(
                currentLength,
                offset,
                existing,
                chunk.Span);

            var written = 0;
            while (resumeAt + written < chunk.Length)
            {
                ct.ThrowIfCancellationRequested();
                var writeOffset = resumeAt + written;
                var count = Math.Min(1024 * 1024, chunk.Length - writeOffset);
                var bytes = chunk.Slice(writeOffset, count).ToArray();
                var writeStatus = session.Store.WriteFile(
                    out var bytesWritten,
                    handle,
                    checked(offset + writeOffset),
                    bytes);
                if (writeStatus != NTStatus.STATUS_SUCCESS)
                    throw new IOException($"SMB chunk 書き込みエラー: {writeStatus}");
                if (bytesWritten <= 0 || bytesWritten > count)
                    throw new IOException($"SMB chunk 書き込み長が不正です: {bytesWritten}");
                written += bytesWritten;
            }

            if (written > 0)
            {
                ct.ThrowIfCancellationRequested();
                var flushStatus = session.Store.FlushFileBuffers(handle);
                if (flushStatus != NTStatus.STATUS_SUCCESS)
                {
                    session.DisposeReal();
                    throw new IOException($"SMB chunk フラッシュエラー: {flushStatus}");
                }
            }

            return new TransferChunkWriteResult(
                offset,
                chunk.Length,
                written,
                Math.Max(currentLength, checked(offset + chunk.Length)),
                resumeAt == chunk.Length);
        }
        finally
        {
            CloseTransferHandle(session, handle);
        }
    }

    /// <summary>リパースポイントを追跡せず、通常ファイル全体の SHA-256 を計算する。</summary>
    public virtual TransferSha256Result ComputeSha256(
        CifsConnectionInfo info,
        string path,
        CancellationToken ct = default)
    {
        using var stream = OpenVerifiedRead(info, path, offset: 0, length: null, ct);
        return TransferHashing.ComputeSha256(stream, ct);
    }

    /// <summary>
    /// Watashi 一時ファイルを同じ親の正式パスへ SMB rename で原子的にcommitする。
    /// 一時ファイル自身を FILE_OPEN_REPARSE_POINT で開き、通常ファイル以外は拒否する。
    /// </summary>
    public virtual void CommitTemp(
        CifsConnectionInfo info,
        string tempPath,
        string targetPath,
        bool replaceIfExists,
        CancellationToken ct = default)
    {
        var paths = TransferV2Validation.ValidateCommitPaths(tempPath, targetPath);
        ct.ThrowIfCancellationRequested();
        using var session = Acquire(info, ct);
        var status = OpenTransferHandle(
            session,
            paths.TempPath,
            AccessMask.DELETE | AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            ShareAccess.None,
            CreateDisposition.FILE_OPEN,
            out var handle,
            requireFile: true);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"commit対象の一時ファイルを開けません: {status}");
        try
        {
            var metadata = ReadTransferMetadata(session, handle, paths.TempPath, ct);
            TransferV2Validation.EnsureRegularFile(metadata, paths.TempPath);
            var rename = new FileRenameInformationType2
            {
                ReplaceIfExists = replaceIfExists,
                FileName = ToSmbFile(paths.TargetPath),
            };
            ct.ThrowIfCancellationRequested();
            var setStatus = session.Store.SetFileInformation(handle, rename);
            if (setStatus != NTStatus.STATUS_SUCCESS)
                throw new IOException($"一時ファイルのcommitに失敗しました: {setStatus}");
        }
        finally
        {
            CloseTransferHandle(session, handle);
        }
    }

    public Stream OpenWrite(CifsConnectionInfo info, string path, CancellationToken ct = default)
    {
        var session = Acquire(info, ct);
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

    public void Delete(CifsConnectionInfo info, string path, CancellationToken ct = default)
    {
        using var session = Acquire(info, ct);
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
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_OPEN_REPARSE_POINT |
                    CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
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

        public NTStatus TryDeleteReparsePoint(string path, bool isDirectory)
        {
            var smbPath = isDirectory ? ToSmbDirectory(path) : ToSmbFile(path);
            var typeOption = isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE;
            var status = _session.Store.CreateFile(
                out object handle, out FileStatus _, smbPath,
                AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                typeOption | CreateOptions.FILE_OPEN_REPARSE_POINT |
                    CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            return status == NTStatus.STATUS_SUCCESS ? _session.Store.CloseFile(handle) : status;
        }

        public bool IsReparsePoint(string path, bool isDirectory)
        {
            var smbPath = isDirectory ? ToSmbDirectory(path) : ToSmbFile(path);
            var typeOption = isDirectory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE;
            var status = _session.Store.CreateFile(
                out object handle, out FileStatus _, smbPath,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                typeOption | CreateOptions.FILE_OPEN_REPARSE_POINT | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"リパースポイント属性の取得用に開けません: {status}");

            try
            {
                var infoStatus = _session.Store.GetFileInformation(
                    out FileInformation info, handle, FileInformationClass.FileAttributeTagInformation);
                if (infoStatus != NTStatus.STATUS_SUCCESS || info is not FileAttributeTagInformation attributes)
                    throw new IOException($"リパースポイント属性の取得エラー: {infoStatus}");
                return (attributes.FileAttributes & FileAttributes.ReparsePoint) != 0;
            }
            finally
            {
                try { _session.Store.CloseFile(handle); } catch { }
            }
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
                        (item.FileAttributes & FileAttributes.Directory) != 0,
                        (item.FileAttributes & FileAttributes.ReparsePoint) != 0))
                    .ToList();
            }
            finally
            {
                try { _session.Store.CloseFile(dirHandle); } catch { }
            }
        }
    }

    public void Rename(CifsConnectionInfo info, string oldPath, string newPath, bool replaceIfExists = false, CancellationToken ct = default)
    {
        using var session = Acquire(info, ct);
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
            var rename = new FileRenameInformationType2 { ReplaceIfExists = replaceIfExists, FileName = smbNew };
            var setStatus = session.Store.SetFileInformation(handle, rename);
            if (setStatus != NTStatus.STATUS_SUCCESS)
                throw new IOException($"リネームエラー: {setStatus}");
        }
        finally
        {
            try { session.Store.CloseFile(handle); } catch { }
        }
    }

    public void Mkdir(CifsConnectionInfo info, string path, CancellationToken ct = default)
    {
        using var session = Acquire(info, ct);
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

    public bool TestConnection(CifsConnectionInfo info, CancellationToken ct = default)
    {
        try
        {
            using var _ = Acquire(info, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private long MeasureDirectoryTree(
        CifsConnectionInfo info,
        string directoryPath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long total = 0;
        foreach (var entry in List(info, directoryPath, ct))
        {
            ct.ThrowIfCancellationRequested();
            var child = PathHelper.NormalizePath($"{directoryPath}/{entry.Name}");
            if (entry.IsReparsePoint)
                throw new TransferReparsePointException(child);
            total = entry.Type == FileEntryTypes.Directory
                ? checked(total + MeasureDirectoryTree(info, child, ct))
                : checked(total + (entry.Size ?? throw new IOException($"ファイルサイズを取得できません: {child}")));
        }
        return total;
    }

    private void EnsureNoReparseAncestors(
        CifsConnectionInfo info,
        string path,
        bool includeLeaf,
        CancellationToken ct)
    {
        var normalized = PathHelper.NormalizePath(path);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var count = includeLeaf ? segments.Length : Math.Max(0, segments.Length - 1);
        var current = string.Empty;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            current += "/" + segments[i];
            var metadata = GetTransferMetadata(info, current, ct);
            if (!metadata.Exists)
                throw new DirectoryNotFoundException($"パス要素が見つかりません: {current}");
            if (metadata.IsReparsePoint)
                throw new TransferReparsePointException(current);
            if (i < count - 1 && metadata.Type != TransferFileTypes.Directory)
                throw new IOException($"パス要素がディレクトリではありません: {current}");
        }
    }

    private void EnsureManagedTrashRoot(CifsConnectionInfo info, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var session = Acquire(info, ct);
        var status = session.Store.CreateFile(
            out object handle,
            out FileStatus _,
            ToSmbDirectory(RemoteTrashPathPolicy.RootPath),
            AccessMask.GENERIC_READ | AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
            FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN_IF,
            CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_OPEN_REPARSE_POINT |
                CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
            null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"管理用ごみ箱を確保できません: {status}");
        try
        {
            var metadata = ReadTransferMetadata(session, handle, RemoteTrashPathPolicy.RootPath, ct);
            if (metadata.IsReparsePoint)
                throw new TransferReparsePointException(RemoteTrashPathPolicy.RootPath);
            if (metadata.Type != TransferFileTypes.Directory)
                throw new IOException("管理用ごみ箱パスがディレクトリではありません。");
        }
        finally
        {
            CloseTransferHandle(session, handle);
        }
    }

    private void AtomicRenameManaged(
        CifsConnectionInfo info,
        string sourcePath,
        string targetPath,
        bool replaceIfExists,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var session = Acquire(info, ct);
        var status = OpenTransferHandle(
            session,
            sourcePath,
            AccessMask.DELETE | AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN,
            out var handle);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"管理用renameの対象を開けません: {status}");
        try
        {
            var metadata = ReadTransferMetadata(session, handle, sourcePath, ct);
            if (metadata.IsReparsePoint)
                throw new TransferReparsePointException(sourcePath);
            ct.ThrowIfCancellationRequested();
            var rename = new FileRenameInformationType2
            {
                ReplaceIfExists = replaceIfExists,
                FileName = ToSmbFile(targetPath),
            };
            var renameStatus = session.Store.SetFileInformation(handle, rename);
            if (renameStatus != NTStatus.STATUS_SUCCESS)
                throw new IOException($"管理用renameに失敗しました: {renameStatus}");
        }
        finally
        {
            CloseTransferHandle(session, handle);
        }
    }

    private Stream OpenVerifiedRead(
        CifsConnectionInfo info,
        string path,
        long offset,
        int? length,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = Acquire(info, ct);
        object? handle = null;
        try
        {
            var status = OpenTransferHandle(
                session,
                path,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                ShareAccess.Read,
                CreateDisposition.FILE_OPEN,
                out handle,
                requireFile: true);
            if (IsMissing(status)) throw new FileNotFoundException("Transfer v2 の対象ファイルが見つかりません。", path);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"Transfer v2 読み取り用にファイルを開けません: {status}");

            var metadata = ReadTransferMetadata(session, handle, path, ct);
            TransferV2Validation.EnsureRegularFile(metadata, path);
            var fileSize = metadata.Size ?? 0;
            if (offset > fileSize) throw new TransferRangeNotSatisfiableException(offset, fileSize);
            var streamLength = length.HasValue
                ? Math.Min((long)length.Value, fileSize - offset)
                : fileSize - offset;
            return new SmbReadStream(session, handle, offset, streamLength);
        }
        catch
        {
            if (handle is not null)
            {
                CloseTransferHandle(session, handle);
            }
            session.Dispose();
            throw;
        }
    }

    private static NTStatus OpenTransferHandle(
        CifsSession session,
        string path,
        AccessMask access,
        ShareAccess shareAccess,
        CreateDisposition disposition,
        out object handle,
        bool requireFile = false)
    {
        var options = CreateOptions.FILE_OPEN_REPARSE_POINT | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT;
        if (requireFile) options |= CreateOptions.FILE_NON_DIRECTORY_FILE;
        return session.Store.CreateFile(
            out handle,
            out FileStatus _,
            ToSmbFile(path),
            access,
            FileAttributes.Normal,
            shareAccess,
            disposition,
            options,
            null);
    }

    private static TransferFileMetadata ReadTransferMetadata(
        CifsSession session,
        object handle,
        string path,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var standardStatus = session.Store.GetFileInformation(
            out FileInformation standardInfo,
            handle,
            FileInformationClass.FileStandardInformation);
        if (standardStatus != NTStatus.STATUS_SUCCESS || standardInfo is not FileStandardInformation standard)
            throw new IOException($"ファイルサイズ・種別の取得エラー ({path}): {standardStatus}");

        ct.ThrowIfCancellationRequested();
        var basicStatus = session.Store.GetFileInformation(
            out FileInformation basicInfo,
            handle,
            FileInformationClass.FileBasicInformation);
        if (basicStatus != NTStatus.STATUS_SUCCESS || basicInfo is not FileBasicInformation basic)
            throw new IOException($"ファイル日時・属性の取得エラー ({path}): {basicStatus}");

        DateTime? lastWrite = basic.LastWriteTime;
        if (lastWrite.HasValue)
            lastWrite = DateTime.SpecifyKind(lastWrite.Value, DateTimeKind.Utc);
        var isReparse = (basic.FileAttributes & FileAttributes.ReparsePoint) != 0;
        if (!standard.Directory && standard.EndOfFile < 0)
            throw new IOException($"ファイルサイズが不正です ({path}): {standard.EndOfFile}");
        return new TransferFileMetadata(
            true,
            standard.Directory ? TransferFileTypes.Directory : TransferFileTypes.File,
            standard.Directory ? null : standard.EndOfFile,
            lastWrite,
            isReparse);
    }

    private static byte[] ReadExact(
        CifsSession session,
        object handle,
        long offset,
        int length,
        CancellationToken ct)
    {
        var result = new byte[length];
        var read = 0;
        while (read < length)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(1024 * 1024, length - read);
            var status = session.Store.ReadFile(out var bytes, handle, checked(offset + read), count);
            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_BUFFER_OVERFLOW)
                throw new IOException($"既存 chunk の読み戻しエラー: {status}");
            if (bytes is null || bytes.Length == 0)
                throw new IOException("既存 chunk の読み戻し中に予期しない EOF へ到達しました。");
            if (bytes.Length > count)
                throw new IOException($"既存 chunk の読み戻し長が不正です: {bytes.Length}");
            Buffer.BlockCopy(bytes, 0, result, read, bytes.Length);
            read += bytes.Length;
        }
        return result;
    }

    private static bool IsMissing(NTStatus status)
        => status is NTStatus.STATUS_NO_SUCH_FILE
            or NTStatus.STATUS_OBJECT_NAME_NOT_FOUND
            or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;

    private static void CloseTransferHandle(CifsSession session, object handle)
    {
        try
        {
            if (session.Store.CloseFile(handle) != NTStatus.STATUS_SUCCESS)
                session.DisposeReal();
        }
        catch
        {
            session.DisposeReal();
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
