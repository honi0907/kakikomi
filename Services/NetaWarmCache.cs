namespace Kakikomi.Services;

/// <summary>ネタ先頭フレームのウォームキャッシュ（LRU・最大32本）。</summary>
internal sealed class NetaWarmCache : IDisposable
{
    private const int MaxEntries = 32;

    private readonly object _lock = new();
    private readonly Dictionary<string, MediaPlayerPair> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();

    public bool Contains(string path)
    {
        lock (_lock)
            return _entries.ContainsKey(path);
    }

    public bool TryTake(string path, out MediaPlayerPair pair)
    {
        lock (_lock)
        {
            if (_entries.Remove(path, out pair!))
            {
                _lru.Remove(path);
                return true;
            }
        }

        pair = null!;
        return false;
    }

    public void Put(string path, MediaPlayerPair pair)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            pair.Dispose();
            return;
        }

        lock (_lock)
        {
            if (_entries.TryGetValue(path, out var existing))
            {
                _lru.Remove(path);
                existing.Dispose();
            }

            while (_entries.Count >= MaxEntries && _lru.Last is not null)
            {
                var victim = _lru.Last.Value;
                _lru.RemoveLast();
                if (_entries.Remove(victim, out var evicted))
                    evicted.Dispose();
            }

            pair.Path = path;
            _entries[path] = pair;
            _lru.AddFirst(path);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var pair in _entries.Values)
                pair.Dispose();

            _entries.Clear();
            _lru.Clear();
        }
    }

    public void Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        lock (_lock)
        {
            if (_entries.Remove(path, out var pair))
            {
                _lru.Remove(path);
                pair.Dispose();
            }
        }
    }

    public void Dispose() => Clear();
}
