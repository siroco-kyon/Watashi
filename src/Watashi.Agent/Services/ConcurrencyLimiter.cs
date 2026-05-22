namespace Watashi.Agent.Services;

public class ConcurrencyLimiter
{
    private int _current;
    private readonly int _max;
    public ConcurrencyLimiter(int max) { _max = Math.Max(1, max); }

    public IDisposable? TryEnter()
    {
        if (Interlocked.Increment(ref _current) > _max)
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
