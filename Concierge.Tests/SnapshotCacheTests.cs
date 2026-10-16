using Concierge.Shared.Caching;
using Concierge.Shared.Chat;
using Concierge.Shared.Devices;
using Concierge.Shared.Planning;
using Concierge.Shared.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text.Json;

namespace Concierge.Tests;

public sealed class SnapshotCacheTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.TicksPerSecond * 2;
    }
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Warm_reads_expire_and_bounds_evict_least_recently_used()
    {
        var clock = new Clock();
        var cache = new AsyncSnapshotCache<string, string>(2, 4, s => s.Length, clock: clock);
        var loads = 0;
        Task<string> Load() { loads++; return Task.FromResult("xx"); }
        await cache.GetAsync("a", Lifetime, Load);
        await cache.GetAsync("b", Lifetime, Load);
        await cache.GetAsync("a", Lifetime, Load);
        await cache.GetAsync("c", Lifetime, Load);
        Assert.Equal(3, loads);
        Assert.Equal(2, cache.Metrics.Entries);
        Assert.Equal(4, cache.Metrics.ContentWeight);
        await cache.GetAsync("b", Lifetime, Load);
        Assert.Equal(4, loads);
        clock.Advance();
        Assert.Equal(0, cache.Metrics.Entries);
        await cache.GetAsync("b", Lifetime, Load);
        Assert.Equal(5, loads);
    }

    [Fact]
    public async Task Concurrent_reads_share_work_but_cancellation_only_cancels_one_waiter()
    {
        var cache = new AsyncSnapshotCache<string, string>(2, 10, s => s.Length);
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var first = cache.GetAsync("a", Lifetime, () => source.Task, cancel.Token);
        var second = cache.GetAsync("a", Lifetime, () => throw new Exception("Duplicate load"));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        source.SetResult("ok");
        Assert.Equal("ok", await second);
        Assert.Equal(1, cache.Metrics.Loads);
        Assert.Equal(1, cache.Metrics.Joined);
    }

    [Fact]
    public async Task Invalidation_prevents_an_old_inflight_read_becoming_the_new_snapshot()
    {
        var cache = new AsyncSnapshotCache<string, string>(2, 10, s => s.Length);
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetAsync("a", Lifetime, () => source.Task);
        cache.Invalidate();
        Assert.Equal("new", await cache.GetAsync("a", Lifetime, () => Task.FromResult("new")));
        source.SetResult("old");
        Assert.Equal("old", await first);
        Assert.Equal("new", await cache.GetAsync("a", Lifetime, () => throw new Exception("Lost new snapshot")));
    }

    [Fact]
    public async Task Errors_rejected_values_and_oversize_values_are_never_reused()
    {
        var cache = new AsyncSnapshotCache<string, string>(2, 2, s => s.Length, s => s != "no");
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("a", Lifetime,
            () => Task.FromException<string>(new InvalidOperationException())));
        Assert.Equal("ok", await cache.GetAsync("a", Lifetime, () => Task.FromResult("ok")));
        cache.Invalidate();
        await cache.GetAsync("a", Lifetime, () => Task.FromResult("no"));
        Assert.Equal(0, cache.Metrics.Entries);
        await cache.GetAsync("a", Lifetime, () => Task.FromResult("large"));
        Assert.Equal(0, cache.Metrics.Entries);
        Assert.Equal("ok", await cache.GetAsync("a", Lifetime, () => Task.FromResult("ok")));
    }

    [Theory]
    [InlineData("SendChat")]
    [InlineData("Remember")]
    [InlineData("Recall")]
    [InlineData("SetupStart")]
    [InlineData("ResidentStart")]
    [InlineData("WakePhrase")]
    public void User_queries_and_mutations_have_no_cache_lifetime(string verb)
        => Assert.Equal(TimeSpan.Zero, CircleAiReadCachePolicy.LifetimeFor(verb));

    [Theory]
    [InlineData("Capabilities", 30)]
    [InlineData("Abilities", 5)]
    [InlineData("SetupPlan", 5)]
    [InlineData("SetupCensus", 5)]
    [InlineData("Footprint", 5)]
    [InlineData("Permissions", 2)]
    [InlineData("SetupReadiness", 1)]
    [InlineData("SetupProgress", 1)]
    [InlineData("ResidentStatus", 1)]
    public void Service_report_lifetimes_are_explicit_and_bounded(string verb, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), CircleAiReadCachePolicy.LifetimeFor(verb));

    [Fact]
    public async Task Measure_repeated_real_sqlite_reads_against_warm_snapshots()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-cache-measure-{Guid.NewGuid():N}.db");
        await using var provider = new ServiceCollection().AddConciergeChat(path).BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        var source = provider.GetRequiredService<ConversationStore>();
        var cache = new CachedConversationStore(source, new Clock());
        var conversation = await source.StartAsync("local", "Measurement");
        for (var i = 0; i < 40; i++) await source.AppendAsync(conversation.Id, i % 2 == 0 ? "user" : "assistant", new string('x', 200));
        await source.GetAsync(conversation.Id); // Warm SQLite/EF before measuring either route.
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) Assert.Equal(40, (await source.GetAsync(conversation.Id))!.Messages.Count);
        var sourceMs = elapsed.Elapsed.TotalMilliseconds;
        elapsed.Restart();
        await cache.GetAsync(conversation.Id);
        var coldMs = elapsed.Elapsed.TotalMilliseconds;
        elapsed.Restart();
        for (var i = 0; i < 100; i++) Assert.Equal(40, (await cache.GetAsync(conversation.Id))!.Messages.Count);
        var warmMs = elapsed.Elapsed.TotalMilliseconds;
        Assert.Equal(1, cache.ThreadMetrics.Loads);
        Assert.Equal(100, cache.ThreadMetrics.Hits);
        var projectSource = provider.GetRequiredService<SqliteProjectStore>();
        var projectCache = new CachedProjectStore(projectSource, new Clock());
        for (var i = 0; i < 10; i++) await projectSource.CreateAsync(ConciergeProject.Create($"Project {i}", "A practical outcome"));
        await projectSource.ListAsync();
        elapsed.Restart();
        for (var i = 0; i < 100; i++) Assert.Equal(10, (await projectSource.ListAsync()).Count);
        var projectSourceMs = elapsed.Elapsed.TotalMilliseconds;
        await projectCache.ListAsync();
        elapsed.Restart();
        for (var i = 0; i < 100; i++) Assert.Equal(10, (await projectCache.ListAsync()).Count);
        var projectWarmMs = elapsed.Elapsed.TotalMilliseconds;
        Assert.Equal(1, projectCache.Metrics.Loads);
        Assert.Equal(100, projectCache.Metrics.Hits);
        var measurementPath = Environment.GetEnvironmentVariable("CONCIERGE_CACHE_MEASUREMENT")
            ?? Path.Combine(Path.GetTempPath(), "cache-measurement.json");
        File.WriteAllText(measurementPath,
            JsonSerializer.Serialize(new { sourceReads = 100, sourceMs, coldMs, warmReads = 100, warmMs, metrics = cache.ThreadMetrics,
                projectSourceReads = 100, projectSourceMs, projectWarmReads = 100, projectWarmMs, projectMetrics = projectCache.Metrics }));
        await provider.DisposeAsync();
        try { File.Delete(path); } catch (IOException) { /* SQLite pooling can retain a temporary database handle until process exit. */ }
    }

    [Fact]
    public async Task Project_catalogues_are_copies_and_saves_invalidate_without_caching_execution_reads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-project-cache-{Guid.NewGuid():N}", "chat.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeProjects(path).BuildServiceProvider();
        var cache = Assert.IsType<CachedProjectStore>(provider.GetRequiredService<IProjectStore>());
        var project = await cache.CreateAsync(ConciergeProject.Create("Home", "Plan chores") with { Assumptions = new[] { "Original" } });
        var read = Assert.Single(await cache.ListAsync());
        ((IList<string>)read.Assumptions)[0] = "Caller mutation";
        Assert.Equal("Original", Assert.Single(await cache.ListAsync()).Assumptions[0]);
        Assert.Equal(1, cache.Metrics.Loads);
        Assert.Equal(1, cache.Metrics.Hits);
        await cache.SaveAsync(project with { Name = "Updated" });
        Assert.Equal("Updated", Assert.Single(await cache.ListAsync()).Name);
        await Assert.ThrowsAsync<ProjectRevisionConflictException>(() => cache.SaveAsync(project));
        Assert.Equal(0, cache.Metrics.Entries); // A failed write cannot leave a retained catalogue behind.
        Assert.Equal("Updated", Assert.Single(await cache.ListAsync()).Name);
        var loads = cache.Metrics.Loads;
        Assert.Equal("Updated", (await cache.GetAsync(project.Id))!.Name);
        Assert.Equal("Updated", (await cache.GetAsync(project.Id))!.Name);
        Assert.Equal(loads, cache.Metrics.Loads);
        var source = provider.GetRequiredService<SqliteProjectStore>();
        var live = (await source.GetAsync(project.Id))!;
        await source.SaveAsync(live with { Name = "External revision" });
        Assert.Equal("External revision", (await cache.GetAsync(project.Id))!.Name);
        Assert.Equal("Updated", Assert.Single(await cache.ListAsync()).Name); // Only the rendering list may still be a snapshot.
        await cache.ListAsync(includeArchived: true);
        Assert.Equal(loads + 1, cache.Metrics.Loads);
        await cache.CreateStartupAsync(StartupJourney.Create(BusinessJourneyType.NewBusiness, "Business", "Launch"));
        Assert.Single(await cache.ListStartupsAsync());
        Assert.Single(await cache.ListStartupsAsync());
    }

    [Fact]
    public async Task Conversation_snapshots_are_copies_writes_are_immediate_and_external_writes_expire()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-cache-{Guid.NewGuid():N}.db");
        await using var provider = new ServiceCollection().AddConciergeChat(path).BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        var source = provider.GetRequiredService<ConversationStore>();
        var clock = new Clock();
        var cache = new CachedConversationStore(source, clock);
        var conversation = await cache.StartAsync("local", "Original");
        var read = await cache.GetAsync(conversation.Id);
        read!.Title = "Caller mutation";
        Assert.Equal("Original", (await cache.GetAsync(conversation.Id))!.Title);
        Assert.Equal(1, cache.ThreadMetrics.Loads);
        await cache.AppendAsync(conversation.Id, "user", "Fresh write");
        Assert.Equal("Fresh write", Assert.Single((await cache.GetAsync(conversation.Id))!.Messages).Content);
        await source.AppendAsync(conversation.Id, "assistant", "External sync");
        Assert.Single((await cache.GetAsync(conversation.Id))!.Messages);
        Assert.Equal(2, (await cache.DeriveMessagesAsync(conversation.Id)).Count);
        var loadsBeforeRun = cache.ThreadMetrics.Loads;
        using (ToolApprovalRunContext.Enter(conversation.Id, Guid.NewGuid()))
            Assert.Equal(2, (await cache.GetAsync(conversation.Id))!.Messages.Count);
        Assert.Equal(loadsBeforeRun, cache.ThreadMetrics.Loads); // Model execution never consults the presentation snapshot.
        clock.Advance();
        Assert.Equal(2, (await cache.GetAsync(conversation.Id))!.Messages.Count);
        await source.AppendAsync(conversation.Id, "assistant", "Synced update");
        await cache.SynchronizeEventsAsync(conversation.Id, await source.ReadEventsAsync(conversation.Id));
        Assert.Equal(3, (await cache.GetAsync(conversation.Id))!.Messages.Count);
        await cache.ListAsync("local");
        Assert.Empty(await cache.ListAsync("another-owner"));
        await cache.DeleteAsync(conversation.Id);
        Assert.Empty(await cache.ListAsync("local"));
        Assert.Null(await cache.GetAsync(conversation.Id));
        await provider.DisposeAsync();
        try { File.Delete(path); } catch (IOException) { /* SQLite pooling can retain a temporary database handle until process exit. */ }
    }
}
