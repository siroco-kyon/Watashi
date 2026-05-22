using SMBLibrary;

namespace Watashi.Agent.Services.Cifs;

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
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SmbWriteStream));
        int remaining = count;
        int bufferOffset = offset;
        while (remaining > 0)
        {
            int toWrite = Math.Min(remaining, ChunkSize);
            var chunk = new byte[toWrite];
            Buffer.BlockCopy(buffer, bufferOffset, chunk, 0, toWrite);
            var status = _session.Store.WriteFile(out int bytesWritten, _handle, _position, chunk);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException($"SMB 書き込み失敗: {status}");
            if (bytesWritten <= 0) throw new IOException("SMB 書き込み: 0 バイトのみ書き込まれました。");
            _position += bytesWritten;
            bufferOffset += bytesWritten;
            remaining -= bytesWritten;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            try { _session.Store.FlushFileBuffers(_handle); } catch { }
            try { _session.Store.CloseFile(_handle); } catch { }
            _session.Dispose();
        }
        base.Dispose(disposing);
    }
}
