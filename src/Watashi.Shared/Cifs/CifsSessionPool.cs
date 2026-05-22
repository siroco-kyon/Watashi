using System.Collections.Concurrent;

namespace Watashi.Shared.Cifs;

/// <summary>
/// (HostAddress, Port, Username, ShareName) キーで CifsSession を再利用するプール。
/// 操作毎の TCP/SMB ハンドシェイクコストを大幅に削減する。
/// </summary>
public sealed class CifsSessionPool : IDisposable
{
    private readonly TimeSpan _idleTtl;
    private readonly int _maxPerKey;
    private readonly ConcurrentDictionary<string, ConcurrentBag<CifsSession>> _idle = new();
    private readonly Timer _evictionTimer;
    private bool _disposed;

    public CifsSessionPool(TimeSpan? idleTtl = null, int maxPerKey = 4)
    {
        _idleTtl = idleTtl ?? TimeSpan.FromSeconds(60);
        _maxPerKey = maxPerKey;
        _evictionTimer = new Timer(_ => Evict(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public CifsSession Acquire(CifsConnectionInfo info)
    {
        var key = Key(info);
        if (_idle.TryGetValue(key, out var bag))
        {
            while (bag.TryTake(out var session))
            {
                if (session.IsAlive() && (DateTime.UtcNow - session.LastUsedUtc) < _idleTtl)
                {
                    session.AttachPool(s => Return(s));
                    return session;
                }
                session.DisposeReal();
            }
        }
        var fresh = CifsSession.Connect(info);
        fresh.AttachPool(s => Return(s));
        return fresh;
    }

    private void Return(CifsSession session)
    {
        if (_disposed) { session.DisposeReal(); return; }
        session.DetachPool();
        if (!session.IsAlive()) { session.DisposeReal(); return; }

        var key = Key(session.Info);
        var bag = _idle.GetOrAdd(key, _ => new ConcurrentBag<CifsSession>());
        if (bag.Count >= _maxPerKey)
        {
            session.DisposeReal();
            return;
        }
        session.LastUsedUtc = DateTime.UtcNow;
        bag.Add(session);
    }

    private void Evict()
    {
        if (_disposed) return;
        var cutoff = DateTime.UtcNow - _idleTtl;
        foreach (var bag in _idle.Values)
        {
            var keep = new List<CifsSession>();
            while (bag.TryTake(out var s))
            {
                if (s.LastUsedUtc < cutoff || !s.IsAlive()) s.DisposeReal();
                else keep.Add(s);
            }
            foreach (var s in keep) bag.Add(s);
        }
    }

    private static string Key(CifsConnectionInfo info)
        => $"{info.HostAddress}|{info.Port}|{info.Username}|{info.ShareName}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _evictionTimer.Dispose();
        foreach (var bag in _idle.Values)
            while (bag.TryTake(out var s)) s.DisposeReal();
    }
}
