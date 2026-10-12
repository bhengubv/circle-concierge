using System.Collections.Concurrent;
using System.Diagnostics;
using Concierge.Model.Host;

namespace Concierge.Tests;

public sealed class CoalescingProgressReporterTests
{
    [Fact]
    public async Task Reports_are_nonblocking_bounded_and_flush_the_latest_value()
    {
        var published = new ConcurrentQueue<int>();
        var reporter = new CoalescingProgressReporter<int>(async value =>
        {
            await Task.Delay(25);
            published.Enqueue(value);
        }, TimeSpan.FromMilliseconds(40));

        var timer = Stopwatch.StartNew();
        for (var value = 0; value < 5_000; value++)
            reporter.Report(value);
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2),
            $"Reporting blocked for {timer.Elapsed.TotalMilliseconds:0} ms.");

        await reporter.DisposeAsync();

        Assert.True(published.Count <= 60, $"Expected coalescing, but published {published.Count} updates.");
        Assert.Equal(4_999, published.Last());
    }
}
