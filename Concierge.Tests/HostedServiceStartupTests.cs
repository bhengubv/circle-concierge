using System.Diagnostics;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concierge.Tests;

/// <summary>
/// That background work registered with AddHostedService actually runs in a MAUI app.
/// </summary>
/// <remarks>
/// ASP.NET Core starts hosted services because it runs a host. MAUI builds a service provider
/// and no host, so nothing ever calls StartAsync — the registration compiles, resolves, and
/// silently does nothing. The on-device model loader is registered that way, which is why the
/// desktop app sat at "Engine queued for load…" forever while the same code reached the model
/// on the web. Found by attaching to the running desktop app's WebView, not by reading it.
/// </remarks>
public sealed class HostedServiceStartupTests
{
    [Fact]
    public async Task Registered_background_work_is_started()
    {
        var service = new RecordingHostedService();

        await ConciergeHostedServices.StartAllAsync(Provider(service));

        Assert.Equal(1, service.Starts);
    }

    [Fact]
    public async Task Every_registered_service_is_started_not_just_the_first()
    {
        var first = new RecordingHostedService();
        var second = new RecordingHostedService();

        await ConciergeHostedServices.StartAllAsync(Provider(first, second));

        Assert.Equal(1, first.Starts);
        Assert.Equal(1, second.Starts);
    }

    [Fact]
    public async Task One_that_fails_does_not_stop_the_others()
    {
        // A host would tear the app down here. On a phone that is a crash on launch, and the
        // thing that failed is usually optional.
        var survivor = new RecordingHostedService();

        await ConciergeHostedServices.StartAllAsync(Provider(new ThrowingHostedService(), survivor));

        Assert.Equal(1, survivor.Starts);
    }

    [Fact]
    public async Task A_provider_with_no_background_work_is_fine()
    {
        await ConciergeHostedServices.StartAllAsync(new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public void Starting_in_the_background_returns_to_the_caller_immediately()
    {
        // Called from app startup. Blocking here is a black screen on desktop and an ANR on
        // Android — and the model load it kicks off takes minutes.
        var slow = new SlowHostedService();

        var stopwatch = Stopwatch.StartNew();
        ConciergeHostedServices.StartInBackground(Provider(slow));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"blocked for {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Work_started_in_the_background_does_run()
    {
        var service = new RecordingHostedService();

        ConciergeHostedServices.StartInBackground(Provider(service));

        Assert.True(await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void A_failure_in_the_background_does_not_reach_the_app()
    {
        // An unobserved exception on a thread pool thread takes the process down.
        ConciergeHostedServices.StartInBackground(Provider(new ThrowingHostedService()));
    }

    private static IServiceProvider Provider(params IHostedService[] services)
    {
        var collection = new ServiceCollection();
        foreach (var service in services)
        {
            collection.AddSingleton(service);
        }
        return collection.BuildServiceProvider();
    }

    private sealed class RecordingHostedService : IHostedService
    {
        public int Starts { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            Started.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("this one is broken");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SlowHostedService : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
            => await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
