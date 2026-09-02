using CircleAI.Inference;
using Concierge.Ai;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Tests;

/// <summary>
/// Threads and memory on the model-download path.
/// </summary>
/// <remarks>
/// <para>
/// What is being managed here is a multi-gigabyte native object. A leaked generator is not a
/// slow leak that a long-running server sweeps up eventually — it is the weights, twice, on a
/// device that was chosen precisely because it might only have 2 GB. Every case below was a
/// defect in the first version of this code.
/// </para>
/// <para>
/// Nothing here downloads anything. The fetch and the generator are supplied through
/// <see cref="CircleAiChatOptions.ModelFetcher"/> and
/// <see cref="CircleAiChatOptions.GeneratorFactory"/>, because a test that reaches the real
/// ones pulls twenty-one gigabytes — which is exactly how the suite came to hang.
/// </para>
/// </remarks>
public sealed class ModelDownloadLifetimeTests
{
    // ── One at a time ──────────────────────────────────────────────────

    [Fact]
    public async Task A_second_accept_while_one_is_running_is_refused()
    {
        var fetch = new BlockingFetch();
        var runtime = NewRuntime(fetch);
        await runtime.LoadAsync(CancellationToken.None);

        var first = runtime.AcceptDownloadAsync();
        Assert.True(fetch.Started.Wait(TimeSpan.FromSeconds(10)));

        Assert.False(await runtime.AcceptDownloadAsync());

        fetch.Finish();
        Assert.True(await first);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_accepts_build_exactly_one_generator()
    {
        // The leak this is here to stop: two generators, the first replaced and never freed.
        var fetch = new BlockingFetch();
        var generators = new GeneratorLedger();
        var runtime = NewRuntime(fetch, generators);
        await runtime.LoadAsync(CancellationToken.None);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => runtime.AcceptDownloadAsync()))
            .ToArray();

        Assert.True(fetch.Started.Wait(TimeSpan.FromSeconds(10)));
        fetch.Finish();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(accepted => accepted));
        Assert.Equal(1, generators.Built);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task A_replaced_model_is_disposed_not_dropped()
    {
        // Loading twice is what a host does when it re-checks the device. The generator being
        // replaced holds the weights; dropping the reference does not free native memory.
        var generators = new GeneratorLedger();
        var runtime = NewRuntime(new ImmediateFetch(), generators, automatic: true);

        await runtime.LoadAsync(CancellationToken.None);
        var first = generators.Last!;
        await runtime.LoadAsync(CancellationToken.None);

        Assert.Equal(2, generators.Built);
        Assert.True(first.Disposed);
        await runtime.DisposeAsync();
    }

    // ── Shutting down ──────────────────────────────────────────────────

    [Fact]
    public async Task Disposing_stops_a_download_in_flight()
    {
        // Closing the app must not leave a 21 GB transfer running against somebody's data.
        var fetch = new BlockingFetch();
        var runtime = NewRuntime(fetch);
        await runtime.LoadAsync(CancellationToken.None);

        var accepting = runtime.AcceptDownloadAsync();
        Assert.True(fetch.Started.Wait(TimeSpan.FromSeconds(10)));

        await runtime.DisposeAsync();

        Assert.False(await accepting.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(fetch.WasCancelled);
    }

    [Fact]
    public async Task A_model_built_as_the_app_closes_is_not_left_resident()
    {
        // The window between "the model finished loading" and "publish it": disposal can land
        // there, and the weights are already in memory by then.
        var fetch = new BlockingFetch();
        var generators = new GeneratorLedger();
        var runtime = NewRuntime(fetch, generators);
        await runtime.LoadAsync(CancellationToken.None);

        var accepting = runtime.AcceptDownloadAsync();
        Assert.True(fetch.Started.Wait(TimeSpan.FromSeconds(10)));
        await runtime.DisposeAsync();
        fetch.Finish();
        await accepting;

        Assert.All(generators.All, generator => Assert.True(generator.Disposed));
    }

    [Fact]
    public async Task The_model_is_freed_on_disposal()
    {
        var generators = new GeneratorLedger();
        var runtime = NewRuntime(new ImmediateFetch(), generators);
        await runtime.LoadAsync(CancellationToken.None);
        await runtime.AcceptDownloadAsync();

        await runtime.DisposeAsync();

        Assert.True(generators.Last!.Disposed);
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var runtime = NewRuntime(new ImmediateFetch());
        await runtime.LoadAsync(CancellationToken.None);

        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Accepting_after_disposal_is_refused_rather_than_throwing()
    {
        var runtime = NewRuntime(new ImmediateFetch());
        await runtime.LoadAsync(CancellationToken.None);
        await runtime.DisposeAsync();

        Assert.False(await runtime.AcceptDownloadAsync());
    }

    [Fact]
    public async Task Streaming_after_disposal_answers_instead_of_touching_a_disposed_gate()
    {
        var runtime = NewRuntime(new ImmediateFetch());
        await runtime.LoadAsync(CancellationToken.None);
        await runtime.DisposeAsync();

        var chunks = new List<string>();
        await foreach (var chunk in runtime.StreamAsync([new ChatTurn("user", "hello")]))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
    }

    // ── Reading state while it changes ─────────────────────────────────

    [Fact]
    public async Task What_is_pending_can_be_read_while_the_runtime_is_working()
    {
        // PendingDownload is read on the render thread while LoadAsync and AcceptDownloadAsync
        // write it on a pool thread. Nothing here may tear or throw.
        var runtime = NewRuntime(new ImmediateFetch());
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = ((IModelDownloadRequired)runtime).PendingDownload?.ModelId;
            }
        });

        await runtime.LoadAsync(CancellationToken.None);
        await runtime.AcceptDownloadAsync();
        await stop.CancelAsync();
        await reader;

        await runtime.DisposeAsync();
    }

    // ── Stopping, and saying where it is up to ─────────────────────────

    [Fact]
    public async Task Stopping_reaches_the_transfer_itself()
    {
        // Not "between steps". A person who has changed their mind about 21 GB needs it to
        // stop now. The overload this used to call takes no cancellation token at all.
        var fetch = new BlockingFetch();
        var runtime = NewRuntime(fetch);
        await runtime.LoadAsync(CancellationToken.None);

        using var stopping = new CancellationTokenSource();
        var accepting = runtime.AcceptDownloadAsync(null, stopping.Token);
        Assert.True(fetch.Started.Wait(TimeSpan.FromSeconds(10)));
        await stopping.CancelAsync();

        Assert.False(await accepting.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(fetch.WasCancelled);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task A_stopped_download_says_so_and_can_be_started_again()
    {
        var fetch = new BlockingFetch();
        var runtime = NewRuntime(fetch);
        await runtime.LoadAsync(CancellationToken.None);

        using var stopping = new CancellationTokenSource();
        var accepting = runtime.AcceptDownloadAsync(null, stopping.Token);
        Assert.True(fetch.Started.Wait(TimeSpan.FromSeconds(10)));
        await stopping.CancelAsync();
        await accepting;

        Assert.Contains("stopped", runtime.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(((IModelDownloadRequired)runtime).PendingDownload);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Progress_carries_a_line_a_person_can_read()
    {
        // A bare fraction is indistinguishable from a hang at the slow end of a connection.
        var reports = new List<ModelDownloadProgress>();
        var runtime = NewRuntime(new ReportingFetch("llm.mnn (2/7)  198 MB / 433 MB  1.7 MB/s  ETA 02:18"));
        await runtime.LoadAsync(CancellationToken.None);

        await runtime.AcceptDownloadAsync(new Progress<ModelDownloadProgress>(reports.Add));

        // Progress<T> posts its callbacks, so give them a moment to land.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (reports.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.False(string.IsNullOrWhiteSpace(report.Description)));
        await runtime.DisposeAsync();
    }

    // ── Fakes ──────────────────────────────────────────────────────────

    private static CircleAiChatRuntime NewRuntime(
        IFetch fetch,
        GeneratorLedger? generators = null,
        bool automatic = false) => new(
        NullLogger<CircleAiChatRuntime>.Instance,
        new CircleAiChatOptions
        {
            ModelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-lifetime-{Guid.NewGuid():N}"),
            AllowAutomaticDownload = automatic,
            ModelFetcher = fetch.FetchAsync,
            GeneratorFactory = (generators ?? new GeneratorLedger()).Build,
        });

    private interface IFetch
    {
        Task<string> FetchAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken);
    }

    /// <summary>A fetch that returns at once.</summary>
    private sealed class ImmediateFetch : IFetch
    {
        public Task<string> FetchAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken)
            => Task.FromResult($"/models/{modelId}");
    }

    /// <summary>A fetch that waits, so a test can act while it is in flight.</summary>
    private sealed class BlockingFetch : IFetch
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Started { get; } = new(false);

        public bool WasCancelled { get; private set; }

        public void Finish() => _release.TrySetResult();

        public async Task<string> FetchAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            Started.Set();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }

            return $"/models/{modelId}";
        }
    }

    /// <summary>A fetch that reports progress the way a real one does.</summary>
    private sealed class ReportingFetch(string description) : IFetch
    {
        public Task<string> FetchAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new ModelDownloadProgress(0.46, description));
            return Task.FromResult($"/models/{modelId}");
        }
    }

    private sealed class GeneratorLedger
    {
        private readonly List<FakeGenerator> _built = [];

        public IReadOnlyList<FakeGenerator> All
        {
            get { lock (_built) { return _built.ToArray(); } }
        }

        public int Built
        {
            get { lock (_built) { return _built.Count; } }
        }

        public FakeGenerator? Last
        {
            get { lock (_built) { return _built.Count == 0 ? null : _built[^1]; } }
        }

        public IChatGenerator Build(string modelPath)
        {
            var generator = new FakeGenerator();
            lock (_built)
            {
                _built.Add(generator);
            }

            return generator;
        }
    }

    private sealed class FakeGenerator : IChatGenerator
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        public Task<string> GenerateAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            CancellationToken ct = default)
            => Task.FromResult("ok");

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return "ok";
        }
    }
}
