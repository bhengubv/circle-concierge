using System.Text.Json;
using Concierge.Shared.Butler;
using Concierge.Shared.Chat;
using Concierge.Shared.Planning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Concierge.Tests;

public sealed class ProjectButlerRunMonitorTests
{
    [Fact]
    public async Task Hosted_monitor_recovers_persisted_run_and_persists_remote_status_events_and_result()
    {
        var api = new FakeButlerApi();
        var database = Path.Combine(Path.GetTempPath(), $"butler-monitor-host-{Guid.NewGuid():N}", "concierge.db");
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<IButlerWorkRunApi>(api);
            services.AddConciergeChat(database);
        }).Build();
        var store = host.Services.GetRequiredService<IProjectStore>();
        var tracked = new ProjectButlerRun
        {
            ConciergeRunId = Guid.NewGuid(), ButlerRunId = api.RunId, OperationId = "find_jobs",
            Summary = "Search roles", Status = "running", LastMonitorError = "network timeout",
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Recruitment", "Find roles") with
        {
            ButlerRuns = [tracked],
        });
        var monitor = host.Services.GetRequiredService<ProjectButlerRunMonitor>();
        var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, _) => reconciled.TrySetResult();
        await host.StartAsync();
        await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(8));

        var saved = Assert.Single((await store.GetAsync(project.Id))!.ButlerRuns);
        Assert.Equal("completed", saved.Status);
        Assert.Null(saved.LastMonitorError);
        Assert.Equal(0, saved.LastEventSequence);
        Assert.Equal("search complete", JsonDocument.Parse(saved.ResultJson!).RootElement.GetProperty("message").GetString());
        Assert.Equal("results ready", saved.Events[0].Data is { } data
            ? JsonDocument.Parse(data).RootElement.GetProperty("stage").GetString()
            : null);
        Assert.Equal(1, api.GetRunCalls);
        await host.StopAsync();
    }

    [Fact]
    public async Task Butler_unreachable_keeps_last_confirmed_state_and_exposes_monitor_error()
    {
        var api = new FakeButlerApi { Failure = new HttpRequestException("service offline") };
        using var provider = CreateProvider(api);
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Recruitment", "Find roles") with
        {
            ButlerRuns =
            [new ProjectButlerRun
            {
                ConciergeRunId = Guid.NewGuid(), ButlerRunId = api.RunId, OperationId = "find_jobs",
                Summary = "Search roles", Status = "running",
            }],
        });
        var monitor = new ProjectButlerRunMonitor(store, api,
            provider.GetRequiredService<ILogger<ProjectButlerRunMonitor>>());

        await monitor.ReconcileOnceAsync();

        var saved = Assert.Single((await store.GetAsync(project.Id))!.ButlerRuns);
        Assert.Equal("running", saved.Status);
        Assert.Contains("service offline", saved.LastMonitorError);
    }

    [Fact]
    public async Task Monitor_recovers_accepted_but_unacknowledged_dispatch_by_idempotency_key_without_resubmitting()
    {
        using var request = JsonDocument.Parse("{}");
        using var result = JsonDocument.Parse("{}");
        var api = new FakeButlerApi
        {
            Recovered = new ButlerWorkRunEnvelope(new ButlerWorkRun(Guid.NewGuid(), "test-user", Guid.NewGuid(),
                "stable-key", null, null, null, null, "find_jobs", "AskFirst", "queued",
                request.RootElement.Clone(), result.RootElement.Clone(), null, false,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), []),
        };
        using var provider = CreateProvider(api);
        var store = provider.GetRequiredService<IProjectStore>();
        var pending = new ProjectButlerRun
        {
            ConciergeRunId = Guid.NewGuid(), IdempotencyKey = "stable-key", RequestJson = "{}",
            OperationId = "find_jobs", Summary = "Search roles", Status = "dispatch_pending",
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Recruitment", "Find roles") with { ButlerRuns = [pending] });
        var monitor = new ProjectButlerRunMonitor(store, api, provider.GetRequiredService<ILogger<ProjectButlerRunMonitor>>());

        await monitor.ReconcileOnceAsync();

        var saved = Assert.Single((await store.GetAsync(project.Id))!.ButlerRuns);
        Assert.Equal(api.Recovered.Run.Id, saved.ButlerRunId);
        Assert.Null(saved.RequestJson);
        Assert.Equal("completed", saved.Status);
        Assert.Equal(0, api.SubmitCalls);
    }

    [Fact]
    public async Task Monitor_closes_cancelled_pending_intent_without_starting_work_when_Butler_has_no_run()
    {
        var api = new FakeButlerApi();
        using var provider = CreateProvider(api);
        var store = provider.GetRequiredService<IProjectStore>();
        var pending = new ProjectButlerRun
        {
            ConciergeRunId = Guid.NewGuid(), IdempotencyKey = "cancelled-key", OperationId = "find_jobs",
            Summary = "Search roles", Status = "dispatch_pending", CancellationRequested = true,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Recruitment", "Find roles") with { ButlerRuns = [pending] });
        var monitor = new ProjectButlerRunMonitor(store, api, provider.GetRequiredService<ILogger<ProjectButlerRunMonitor>>());

        await monitor.ReconcileOnceAsync();

        var saved = Assert.Single((await store.GetAsync(project.Id))!.ButlerRuns);
        Assert.Equal("cancelled", saved.Status);
        Assert.Equal(Guid.Empty, saved.ButlerRunId);
        Assert.Equal(0, api.SubmitCalls);
    }

    private static ServiceProvider CreateProvider(FakeButlerApi api)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IButlerWorkRunApi>(api);
        services.AddConciergeChat(Path.Combine(Path.GetTempPath(), $"butler-monitor-{Guid.NewGuid():N}", "concierge.db"));
        return services.BuildServiceProvider();
    }

    private sealed class FakeButlerApi : IButlerWorkRunApi
    {
        public Guid RunId { get; } = Guid.NewGuid();
        public Exception? Failure { get; init; }
        public int GetRunCalls { get; private set; }
        public int SubmitCalls { get; private set; }
        public ButlerWorkRunEnvelope? Recovered { get; init; }

        public Task<ButlerWorkCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ButlerWorkCapabilities("1", []));
        public Task<ButlerWorkRunAccepted> SubmitAsync(ButlerWorkRunRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            return Task.FromResult(new ButlerWorkRunAccepted(RunId, request.ConciergeRunId, "queued", "status", "events"));
        }
        public Task<ButlerWorkRunEnvelope?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Recovered);
        public Task<ButlerWorkRunEnvelope> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            GetRunCalls++;
            if (Failure is not null) throw Failure;
            using var request = JsonDocument.Parse("{}");
            using var result = JsonDocument.Parse("""{"message":"search complete"}""");
            var run = new ButlerWorkRun(runId, "test-user", Guid.NewGuid(), "test-key", null, null, null,
                null, "find_jobs", "AskFirst", "completed", request.RootElement.Clone(), result.RootElement.Clone(),
                null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return Task.FromResult(new ButlerWorkRunEnvelope(run, []));
        }
        public Task<IReadOnlyList<ButlerWorkRunEvent>> ReadEventsAsync(Guid runId, long afterSequence,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ButlerWorkRunEvent> events = afterSequence < 0
                ? [new ButlerWorkRunEvent(0, "progress", JsonDocument.Parse("""{"stage":"results ready"}""").RootElement.Clone(), DateTimeOffset.UtcNow)]
                : [];
            return Task.FromResult(events);
        }
        public Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
