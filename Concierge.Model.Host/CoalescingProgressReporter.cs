using System.Threading.Channels;

namespace Concierge.Model.Host;

/// <summary>
/// Relays progress without blocking the producer and keeps only the newest pending
/// update when the destination is slower than the producer.
/// </summary>
public sealed class CoalescingProgressReporter<T> : IProgress<T>, IAsyncDisposable
{
    private readonly Channel<T> _updates = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly Func<T, Task> _publish;
    private readonly TimeSpan _minimumInterval;
    private readonly Task _pump;

    public CoalescingProgressReporter(Func<T, Task> publish, TimeSpan? minimumInterval = null)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(250);
        if (_minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        _pump = PumpAsync();
    }

    /// <summary>Queues the latest value. This call never waits for the destination.</summary>
    public void Report(T value) => _updates.Writer.TryWrite(value);

    /// <summary>Flushes the newest queued value and completes the single writer.</summary>
    public async ValueTask DisposeAsync()
    {
        _updates.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (var initial in _updates.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var latest = initial;
            if (_minimumInterval > TimeSpan.Zero)
                await Task.Delay(_minimumInterval).ConfigureAwait(false);

            while (_updates.Reader.TryRead(out var newer))
                latest = newer;

            await _publish(latest).ConfigureAwait(false);
        }
    }
}
