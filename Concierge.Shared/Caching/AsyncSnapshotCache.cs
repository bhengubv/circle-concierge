namespace Concierge.Shared.Caching;

public sealed record SnapshotCacheMetrics(long Hits, long Loads, long Joined, int Entries, long ContentWeight);

/// <summary>Bounded read snapshots. Shared loads survive one cancelled reader; invalidation wins over old loads.</summary>
public sealed class AsyncSnapshotCache<TKey, TValue>(int maximumEntries, long maximumWeight,
    Func<TValue, long> weight, Func<TValue, bool>? cacheable = null, TimeProvider? clock = null) where TKey : notnull
{
    private sealed record Entry(TValue Value, long Stamp, TimeSpan Lifetime, long Weight, long Access);
    private sealed record Flight(long Generation, TaskCompletionSource<TValue> Completion);
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = [];
    private readonly Dictionary<TKey, Flight> _flights = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _generation, _access, _hits, _loads, _joined, _weight;

    public SnapshotCacheMetrics Metrics { get { lock (_gate) { Prune(); return new(_hits, _loads, _joined, _entries.Count, _weight); } } }
    public void Invalidate()
    {
        lock (_gate) { _generation++; _entries.Clear(); _weight = 0; }
    }
    public Task<TValue> GetAsync(TKey key, TimeSpan lifetime, Func<Task<TValue>> load, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Flight flight;
        var launch = false;
        lock (_gate)
        {
            Prune();
            if (_entries.TryGetValue(key, out var entry))
            {
                _hits++;
                _entries[key] = entry with { Access = ++_access };
                return Task.FromResult(entry.Value);
            }
            if (_flights.TryGetValue(key, out flight!) && flight.Generation == _generation && !flight.Completion.Task.IsCompleted) _joined++;
            else
            {
                _loads++;
                flight = new(_generation, new(TaskCreationOptions.RunContinuationsAsynchronously));
                // Uncached overflow loads are not retained in the cache's flight table.
                if (_flights.Count < maximumEntries || _flights.ContainsKey(key)) _flights[key] = flight;
                launch = true;
            }
        }
        // SQLite's async APIs can execute synchronously. Keep cold reads off the rendering thread.
        if (launch) _ = Task.Run(() => FillAsync(key, lifetime, load, flight));
        return flight.Completion.Task.WaitAsync(token);
    }
    private async Task FillAsync(TKey key, TimeSpan lifetime, Func<Task<TValue>> load, Flight flight)
    {
        try
        {
            var value = await load().ConfigureAwait(false);
            var size = Math.Max(0, weight(value));
            lock (_gate)
            {
                if (flight.Generation == _generation && lifetime > TimeSpan.Zero && maximumEntries > 0
                    && size <= maximumWeight && (cacheable?.Invoke(value) ?? true))
                {
                    Prune();
                    while (_entries.Count > 0 && (_entries.Count >= maximumEntries || _weight + size > maximumWeight))
                        Remove(_entries.MinBy(pair => pair.Value.Access).Key);
                    if (_entries.ContainsKey(key)) Remove(key);
                    _entries[key] = new(value, _clock.GetTimestamp(), lifetime, size, ++_access);
                    _weight += size;
                }
            }
            flight.Completion.TrySetResult(value);
        }
        catch (Exception error) { flight.Completion.TrySetException(error); }
        finally
        {
            lock (_gate)
                if (_flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) _flights.Remove(key);
        }
    }
    private void Prune()
    {
        foreach (var key in _entries.Where(p => _clock.GetElapsedTime(p.Value.Stamp) >= p.Value.Lifetime).Select(p => p.Key).ToArray()) Remove(key);
    }
    private void Remove(TKey key) { if (_entries.Remove(key, out var entry)) _weight -= entry.Weight; }
}
