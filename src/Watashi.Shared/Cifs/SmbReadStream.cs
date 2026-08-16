using SMBLibrary;

namespace Watashi.Shared.Cifs;

public sealed class SmbReadStream : Stream
{
    private const int ChunkSize = 1024 * 1024;
    private readonly CifsSession _session;
    private readonly object _handle;
    private readonly long _startOffset;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    public SmbReadStream(CifsSession session, object handle, long length)
        : this(session, handle, startOffset: 0, length: length)
    {
    }

    public SmbReadStream(CifsSession session, object handle, long startOffset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (startOffset > long.MaxValue - length)
            throw new ArgumentOutOfRangeException(nameof(length), "startOffset + length が Int64 の範囲を超えています。");
        _session = session;
        _handle = handle;
        _startOffset = startOffset;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SmbReadStream));
        if (count == 0) return 0;
        if (_position >= _length) return 0;
        int toRead = (int)Math.Min(Math.Min(count, ChunkSize), _length - _position);
        var status = _session.Store.ReadFile(out byte[] data, _handle, _startOffset + _position, toRead);
        if (status == NTStatus.STATUS_END_OF_FILE) return 0;
        if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_BUFFER_OVERFLOW)
            throw new IOException($"SMB 読み取りエラー: {status}");
        if (data == null || data.Length == 0) return 0;
        if (data.Length > toRead)
            throw new IOException($"SMB 読み取り長が要求を超えています (requested={toRead}, actual={data.Length})。");
        Buffer.BlockCopy(data, 0, buffer, offset, data.Length);
        _position += data.Length;
        return data.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            var reusable = false;
            try { reusable = _session.Store.CloseFile(_handle) == NTStatus.STATUS_SUCCESS; }
            catch { }
            if (reusable) _session.Dispose();
            else _session.DisposeReal();
        }
        base.Dispose(disposing);
    }
}
