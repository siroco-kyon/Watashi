namespace Watashi.Agent.Services;

public class ConcurrencyLimiter
{
    private int _current;
    private int _max;
    public ConcurrencyLimiter(int max) { _max = Math.Max(1, max); }

    public int Max => Volatile.Read(ref _max);

    /// <summary>
    /// 上限値を更新する。Heartbeat 応答で中央サーバから受け取った値を反映する経路で使う。
    /// 既に取得済みのリースに影響はないが、その後の <see cref="TryEnter"/> から新しい上限が効く。
    /// </summary>
    public void SetMax(int max)
    {
        var v = Math.Max(1, max);
        Interlocked.Exchange(ref _max, v);
    }

    public IDisposable? TryEnter()
    {
        if (Interlocked.Increment(ref _current) > Volatile.Read(ref _max))
        {
            Interlocked.Decrement(ref _current);
            return null;
        }
        return new Lease(this);
    }

    private void Exit() => Interlocked.Decrement(ref _current);

    private sealed class Lease : IDisposable
    {
        private readonly ConcurrencyLimiter _owner;
        private int _disposed;
        public Lease(ConcurrencyLimiter owner) => _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Exit();
        }
    }
}
