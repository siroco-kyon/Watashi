using System.Collections.Concurrent;

namespace Watashi.Server.Services;

/// <summary>
/// key単位で処理を直列化し、最後の利用者が離れたentryを安全に破棄する軽量lock。
/// session/idempotency keyを無期限にdictionaryへ残さない。
/// </summary>
internal sealed class KeyedAsyncLock<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();
    internal int EntryCount => _entries.Count;

    public async ValueTask<IDisposable> AcquireAsync(TKey key, CancellationToken ct)
    {
        Entry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(key, static _ => new Entry());
            lock (entry.Sync)
            {
                if (entry.Removed) continue;
                entry.References++;
                break;
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry, releaseSemaphore: false);
            throw;
        }
    }

    private void Release(TKey key, Entry entry)
        => ReleaseReference(key, entry, releaseSemaphore: true);

    private void ReleaseReference(TKey key, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore) entry.Semaphore.Release();
        var dispose = false;
        lock (entry.Sync)
        {
            entry.References--;
            if (entry.References == 0)
            {
                entry.Removed = true;
                _entries.TryRemove(key, out _);
                dispose = true;
            }
        }
        if (dispose) entry.Semaphore.Dispose();
    }

    private sealed class Entry
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
        public bool Removed { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private KeyedAsyncLock<TKey>? _owner;
        private readonly TKey _key;
        private readonly Entry _entry;

        public Lease(KeyedAsyncLock<TKey> owner, TKey key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_key, _entry);
        }
    }
}
