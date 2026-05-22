using System.IO;
using System.Net;
using System.Net.Http;

namespace Watashi.Client.Services;

public class ProgressStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly int _bufferSize;
    private readonly IProgress<long>? _progress;

    public ProgressStreamContent(Stream source, int bufferSize, IProgress<long>? progress)
    {
        _source = source;
        _bufferSize = bufferSize;
        _progress = progress;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buffer = new byte[_bufferSize];
        long total = 0;
        int n;
        while ((n = await _source.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
            total += n;
            _progress?.Report(total);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_source.CanSeek) { length = _source.Length; return true; }
        length = -1; return false;
    }
}
