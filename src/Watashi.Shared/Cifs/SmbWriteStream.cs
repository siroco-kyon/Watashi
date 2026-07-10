using System.Buffers;
using SMBLibrary;

namespace Watashi.Shared.Cifs;

public sealed class SmbWriteStream : Stream
{
    private const int ChunkSize = 1024 * 1024;
    private readonly CifsSession _session;
    private readonly object _handle;
    private long _position;
    private bool _disposed;

    public SmbWriteStream(CifsSession session, object handle)
    {
        _session = session;
        _handle = handle;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SmbWriteStream));
        int remaining = count;
        int bufferOffset = offset;
        var pool = ArrayPool<byte>.Shared;
        byte[]? rented = null;
        try
        {
            while (remaining > 0)
            {
                int toWrite = Math.Min(remaining, ChunkSize);
                rented ??= pool.Rent(ChunkSize);
                Buffer.BlockCopy(buffer, bufferOffset, rented, 0, toWrite);
                byte[] chunk = toWrite == rented.Length ? rented : Slice(rented, toWrite);
                var status = _session.Store.WriteFile(out int bytesWritten, _handle, _position, chunk);
                if (status != NTStatus.STATUS_SUCCESS)
                    throw new IOException($"SMB 書き込みエラー: {status}");
                if (bytesWritten <= 0)
                    throw new IOException("SMB 書き込み: 0 バイトのみ書き込まれました。");
                _position += bytesWritten;
                bufferOffset += bytesWritten;
                remaining -= bytesWritten;
            }
        }
        finally
        {
            if (rented is not null) pool.Return(rented);
        }
    }

    private static byte[] Slice(byte[] source, int length)
    {
        var dst = new byte[length];
        Buffer.BlockCopy(source, 0, dst, 0, length);
        return dst;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            Exception? failure = null;
            try
            {
                var status = _session.Store.FlushFileBuffers(_handle);
                if (status != NTStatus.STATUS_SUCCESS)
                    failure = new IOException($"SMB フラッシュエラー: {status}");
            }
            catch (Exception ex)
            {
                failure = new IOException("SMB フラッシュ中に例外が発生しました。", ex);
            }

            try
            {
                var status = _session.Store.CloseFile(_handle);
                if (status != NTStatus.STATUS_SUCCESS && failure is null)
                    failure = new IOException($"SMB クローズエラー: {status}");
            }
            catch (Exception ex)
            {
                failure ??= new IOException("SMB クローズ中に例外が発生しました。", ex);
            }

            if (failure is null)
                _session.Dispose();
            else
                _session.DisposeReal();

            if (failure is not null)
                throw failure;
        }
        base.Dispose(disposing);
    }
}
