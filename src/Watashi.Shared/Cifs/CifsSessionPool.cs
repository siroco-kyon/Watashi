using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Watashi.Shared.Cifs;

/// <summary>
/// (HostAddress, Port, Username, Password, ShareName) キーで CifsSession を再利用するプール。
/// 操作毎の TCP/SMB ハンドシェイクコストを大幅に削減する。
/// </summary>
public sealed class CifsSessionPool : IDisposable
{
    private readonly TimeSpan _idleTtl;
    private readonly TimeSpan _acquireTimeout;
    private readonly int _maxPerKey;
    private readonly ConcurrentDictionary<string, ConcurrentBag<CifsSession>> _idle = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly Timer _evictionTimer;
    private volatile bool _disposed;

    public CifsSessionPool(TimeSpan? idleTtl = null, int maxPerKey = 4, TimeSpan? acquireTimeout = null)
    {
        _idleTtl = idleTtl ?? TimeSpan.FromSeconds(60);
        _acquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(30);
        if (_acquireTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(acquireTimeout), "SMB セッション待機タイムアウトは 0 より大きい必要があります。");
        _maxPerKey = Math.Max(1, maxPerKey);
        _evictionTimer = new Timer(_ => Evict(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public CifsSession Acquire(CifsConnectionInfo info, CancellationToken ct = default)
    {
        // Dispose 後に Acquire されると、新しいセッションをプールに紐付けてしまい
        // Return 時に DisposeReal されるだけのデッドフロー。明示的に拒否する。
        if (_disposed) throw new ObjectDisposedException(nameof(CifsSessionPool));
        var key = Key(info);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(_maxPerKey, _maxPerKey));
        WaitForSlot(gate, _acquireTimeout, ct);
        if (_disposed)
        {
            gate.Release();
            throw new ObjectDisposedException(nameof(CifsSessionPool));
        }

        try
        {
            if (_idle.TryGetValue(key, out var bag))
            {
                while (bag.TryTake(out var session))
                {
                    if (session.IsAlive() && (DateTime.UtcNow - session.LastUsedUtc) < _idleTtl)
                    {
                        session.AttachPool(s => Return(s, key, gate));
                        return session;
                    }
                    session.DisposeReal();
                }
            }

            var fresh = CifsSession.Connect(info);
            fresh.AttachPool(s => Return(s, key, gate));
            return fresh;
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    internal static void WaitForSlot(SemaphoreSlim gate, TimeSpan timeout, CancellationToken ct)
    {
        if (!gate.Wait(timeout, ct))
            throw new TimeoutException($"SMB セッションの空きを {timeout.TotalSeconds:0.#} 秒待ちましたが取得できませんでした。");
    }

    private void Return(CifsSession session, string key, SemaphoreSlim gate)
    {
        try
        {
            if (_disposed) { session.DisposeReal(); return; }
            session.DetachPool();
            if (!session.IsAlive()) { session.DisposeReal(); return; }

            var bag = _idle.GetOrAdd(key, _ => new ConcurrentBag<CifsSession>());
            session.LastUsedUtc = DateTime.UtcNow;
            bag.Add(session);
        }
        finally
        {
            gate.Release();
        }
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

    internal static string Key(CifsConnectionInfo info)
    {
        var passwordHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.Password ?? string.Empty)));
        return $"{info.HostAddress}|{info.Port}|{info.Username}|{passwordHash}|{info.ShareName}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _evictionTimer.Dispose();
        foreach (var bag in _idle.Values)
            while (bag.TryTake(out var s)) s.DisposeReal();
    }
}
