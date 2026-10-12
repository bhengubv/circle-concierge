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
    public async Task A_model_recheck_reuses_the_loaded_generator_without_leaking_it()
    {
        // Loading twice is what a host does when it re-checks the device. The generator being
        // replaced holds the weights; dropping the reference does not free native memory.
        var generators = new GeneratorLedger();
        var runtime = NewRuntime(new ImmediateFetch(), generators, automatic: true, modelAlreadyPresent: true);

        await runtime.LoadAsync(CancellationToken.None);
        var first = generators.Last!;
        await runtime.LoadAsync(CancellationToken.None);

        Assert.Equal(1, generators.Built);
        Assert.False(first.Disposed);
        await runtime.DisposeAsync();
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task Staged_models_download_sequentially_from_smallest_to_selected_tier()
    {
        var modelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-staged-download-{Guid.NewGuid():N}");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetch = new RecordingFetch(present);
        var runtime = NewRuntime(
            fetch,
            stageSmallestFirst: true,
            modelAlreadyPresent: false,
            modelsDirectory: modelsDirectory,
            isModelPresent: modelId =>
            {
                lock (present)
                    return present.Contains(modelId);
            });

        try
        {
            await runtime.LoadAsync(CancellationToken.None);
            using var selector = new CircleAiModelSelectorContext(modelsDirectory);
            var probe = CircleAiTestDefaults.Probe(modelsDirectory);
            selector.Assess(probe);
            var selected = selector.BestFit(probe, ChatCapability.Default | ChatCapability.Tools);
            var expected = selector.EligibleCandidatesSmallestFirst(
                    probe, ChatCapability.Default | ChatCapability.Tools)
                .TakeWhile(candidate => !string.Equals(candidate.ModelId, selected.ModelId, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.ModelId)
                .Append(selected.ModelId)
                .ToArray();

            var offer = ((IModelDownloadRequired)runtime).PendingDownload!;
            Assert.Equal(selected.EstimatedBytes, offer.Bytes);
            Assert.Equal(expected.Length, offer.StagedModelCount);
            Assert.Equal(expected.Sum(modelId => selector.AllCandidates(probe)
                .Single(candidate => string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                .EstimatedBytes), offer.StagingBytes);

            Assert.True(await runtime.AcceptDownloadAsync());

            Assert.Equal(expected, fetch.ModelIds);
            Assert.Equal(1, fetch.MaximumConcurrentTransfers);
            Assert.True(runtime.IsReady);
            Assert.Equal(selected.ModelId, runtime.EngineLabel.Split(' ')[0]);
        }
        finally
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(modelsDirectory))
                Directory.Delete(modelsDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Background_owner_can_persist_acceptance_without_starting_a_transfer()
    {
        var modelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-download-job-approval-{Guid.NewGuid():N}");
        var fetch = new RecordingFetch(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var runtime = NewRuntime(fetch, stageSmallestFirst: true, modelsDirectory: modelsDirectory);

        try
        {
            await runtime.LoadAsync(CancellationToken.None);
            var approval = Assert.IsAssignableFrom<IModelDownloadApprovalPreparer>(runtime);
            Assert.True(await approval.PrepareDownloadApprovalAsync());

            Assert.Empty(fetch.ModelIds);
            Assert.True(File.Exists(Path.Combine(modelsDirectory, "download-approval.json")));
            Assert.Contains("background download worker", runtime.CurrentDownloadProgress!.Description);

            await runtime.CancelCurrentDownloadAsync();
            Assert.False(File.Exists(Path.Combine(modelsDirectory, "download-approval.json")));
            Assert.Null(runtime.CurrentDownloadProgress);
        }
        finally
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(modelsDirectory))
                Directory.Delete(modelsDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task An_accepted_staged_download_resumes_after_runtime_recreation()
    {
        var modelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-download-resume-{Guid.NewGuid():N}");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstFetch = new BlockingFetch();
        var firstRuntime = NewRuntime(
            firstFetch,
            stageSmallestFirst: true,
            modelsDirectory: modelsDirectory,
            isModelPresent: modelId =>
            {
                lock (present)
                    return present.Contains(modelId);
            });
        CircleAiChatRuntime? resumedRuntime = null;

        try
        {
            await firstRuntime.LoadAsync(CancellationToken.None);
            var initialOffer = ((IModelDownloadRequired)firstRuntime).PendingDownload!;
            var interrupted = firstRuntime.AcceptDownloadAsync();
            Assert.True(firstFetch.Started.Wait(TimeSpan.FromSeconds(10)));

            await firstRuntime.DisposeAsync();
            Assert.False(await interrupted.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(firstFetch.WasCancelled);
            Assert.True(File.Exists(Path.Combine(modelsDirectory, "download-approval.json")));

            var resumedFetch = new RecordingFetch(present);
            resumedRuntime = NewRuntime(
                resumedFetch,
                stageSmallestFirst: true,
                modelsDirectory: modelsDirectory,
                isModelPresent: modelId =>
                {
                    lock (present)
                        return present.Contains(modelId);
                });
            await resumedRuntime.LoadAsync(CancellationToken.None);

            Assert.True(await resumedRuntime.ResumeAcceptedDownloadAsync());
            Assert.Equal(initialOffer.StagedModelCount, resumedFetch.ModelIds.Count);
            Assert.Equal(1, resumedFetch.MaximumConcurrentTransfers);
            Assert.True(resumedRuntime.IsReady);
            Assert.False(File.Exists(Path.Combine(modelsDirectory, "download-approval.json")));
        }
        finally
        {
            await firstRuntime.DisposeAsync();
            if (resumedRuntime is not null)
                await resumedRuntime.DisposeAsync();
            if (Directory.Exists(modelsDirectory))
                Directory.Delete(modelsDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task A_saved_acceptance_for_a_different_selection_is_preserved_and_explained()
    {
        var modelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-download-stale-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(modelsDirectory, "download-approval.json"),
            """{"schemaVersion":1,"selectedModelId":"old-model","stagedModelIds":["old-model"],"acceptedAtUtc":"2026-10-02T00:00:00+00:00"}""");
        var fetch = new RecordingFetch(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var runtime = NewRuntime(fetch, stageSmallestFirst: true, modelsDirectory: modelsDirectory);

        try
        {
            await runtime.LoadAsync(CancellationToken.None);
            Assert.NotEqual("old-model", runtime.Selected!.ModelId);

            Assert.False(await runtime.ResumeAcceptedDownloadAsync());
            Assert.Empty(fetch.ModelIds);
            Assert.True(File.Exists(Path.Combine(modelsDirectory, "download-approval.json")),
                "A resource-driven plan change must not erase the accepted plan or partial files.");
            Assert.NotNull(((IModelDownloadRequired)runtime).PendingDownload);
            Assert.Contains("paused", runtime.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("model-download-paused", runtime.CapabilityCatalog.Activity?.Kind);
            Assert.Contains("Partial model files are kept", runtime.CapabilityCatalog.Activity?.Description);
        }
        finally
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(modelsDirectory))
                Directory.Delete(modelsDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task First_compatible_model_can_answer_while_the_next_tier_is_still_downloading()
    {
        var modelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-progressive-ready-{Guid.NewGuid():N}");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetch = new HoldAfterFirstFetch(present);
        var runtime = NewRuntime(
            fetch,
            modelAlreadyPresent: false,
            stageSmallestFirst: true,
            modelsDirectory: modelsDirectory,
            isModelPresent: modelId =>
            {
                lock (present)
                    return present.Contains(modelId);
            });

        try
        {
            await runtime.LoadAsync(CancellationToken.None);
            using var selector = new CircleAiModelSelectorContext(modelsDirectory);
            var probe = CircleAiTestDefaults.Probe(modelsDirectory);
            selector.Assess(probe);
            var selected = selector.BestFit(probe, ChatCapability.Default | ChatCapability.Tools);
            var staged = selector.EligibleCandidatesSmallestFirst(probe, ChatCapability.Default | ChatCapability.Tools)
                .TakeWhile(candidate => !string.Equals(candidate.ModelId, selected.ModelId, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.ModelId)
                .Append(selected.ModelId)
                .ToArray();
            Assert.True(staged.Length > 1, "The fixture must offer a smaller starting tier and a later upgrade.");

            var accepting = runtime.AcceptDownloadAsync();
            await fetch.SecondTransferStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(runtime.IsReady);
            Assert.Equal(staged[0], runtime.EngineLabel.Split(' ')[0]);

            var answer = new List<string>();
            using var answerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var chunk in runtime.StreamAsync([new ChatTurn("user", "Can I keep working?")])
                               .WithCancellation(answerTimeout.Token))
                answer.Add(chunk);

            Assert.Equal(new[] { "ok" }, answer);
            Assert.False(accepting.IsCompleted, "The later model transfer should still be blocked while the first-tier answer succeeds.");

            fetch.FinishLaterStages();
            Assert.True(await accepting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(selected.ModelId, runtime.EngineLabel.Split(' ')[0]);
        }
        finally
        {
            fetch.FinishLaterStages();
            await runtime.DisposeAsync();
            if (Directory.Exists(modelsDirectory))
                Directory.Delete(modelsDirectory, recursive: true);
        }
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
    public async Task Runtime_cancel_stops_an_automatic_in_flight_download()
    {
        var fetch = new BlockingFetch();
        var modelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-download-stop-{Guid.NewGuid():N}");
        var runtime = NewRuntime(fetch, modelsDirectory: modelsDirectory);
        try
        {
            await runtime.LoadAsync(CancellationToken.None);

            var accepting = runtime.AcceptDownloadAsync();
            Assert.True(fetch.Started.Wait(TimeSpan.FromSeconds(10)));
            await ((IModelDownloadRequired)runtime).CancelCurrentDownloadAsync();

            Assert.False(await accepting.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(fetch.WasCancelled);
            Assert.Contains("stopped", runtime.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(modelsDirectory, "download-approval.json")),
                "An explicit Stop revokes automatic restart of this download.");
        }
        finally
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(modelsDirectory))
                Directory.Delete(modelsDirectory, recursive: true);
        }
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
    public async Task A_failed_download_can_be_retried_from_the_offer_without_restarting_the_app()
    {
        var fetch = new FailOnceFetch();
        var runtime = NewRuntime(fetch);
        await runtime.LoadAsync(CancellationToken.None);

        Assert.False(await runtime.AcceptDownloadAsync());
        Assert.Contains("retry from this download offer", runtime.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(((IModelDownloadRequired)runtime).PendingDownload);

        Assert.True(await runtime.AcceptDownloadAsync());
        Assert.Equal(2, fetch.Attempts);
        Assert.True(runtime.IsReady);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Progress_carries_a_line_a_person_can_read()
    {
        // A bare fraction is indistinguishable from a hang at the slow end of a connection.
        var reports = new List<ModelDownloadProgress>();
        const string fileProgress = "llm.mnn (2/7)  198 MB / 433 MB  1.7 MB/s  ETA 02:18";
        var runtime = NewRuntime(new ReportingFetch(fileProgress));
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
        Assert.Contains(fileProgress, string.Join("\n", reports.Select(report => report.Description)));
        await runtime.DisposeAsync();
    }

    // ── Fakes ──────────────────────────────────────────────────────────

    private static CircleAiChatRuntime NewRuntime(
        IFetch fetch,
        GeneratorLedger? generators = null,
        bool automatic = false,
        bool modelAlreadyPresent = false,
        bool stageSmallestFirst = false,
        string? modelsDirectory = null,
        Func<string, bool>? isModelPresent = null) => new(
        NullLogger<CircleAiChatRuntime>.Instance,
        new CircleAiChatOptions
        {
            ModelsDirectory = modelsDirectory ?? Path.Combine(Path.GetTempPath(), $"concierge-lifetime-{Guid.NewGuid():N}"),
            DeviceProbeFactory = CircleAiTestDefaults.Probe,
            AllowAutomaticDownload = automatic,
            // Make model discovery deterministic: this test supplies a fake fetcher and
            // generator, so it should not depend on the local Circle AI registry or disk.
            ModelPresence = isModelPresent ?? (_ => modelAlreadyPresent),
            ModelFetcher = fetch.FetchAsync,
            GeneratorFactory = (generators ?? new GeneratorLedger()).Build,
            StageEligibleModelsSmallestFirst = stageSmallestFirst,
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

    private sealed class FailOnceFetch : IFetch
    {
        public int Attempts { get; private set; }

        public Task<string> FetchAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Attempts == 1
                ? Task.FromException<string>(new IOException("The connection dropped."))
                : Task.FromResult($"/models/{modelId}");
        }
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

    private sealed class RecordingFetch(HashSet<string> present) : IFetch
    {
        private readonly List<string> _modelIds = [];
        private int _activeTransfers;
        private int _maximumConcurrentTransfers;

        public IReadOnlyList<string> ModelIds
        {
            get { lock (_modelIds) { return _modelIds.ToArray(); } }
        }

        public int MaximumConcurrentTransfers => Volatile.Read(ref _maximumConcurrentTransfers);

        public async Task<string> FetchAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeTransfers);
            while (active > Volatile.Read(ref _maximumConcurrentTransfers))
            {
                var seen = Volatile.Read(ref _maximumConcurrentTransfers);
                if (Interlocked.CompareExchange(ref _maximumConcurrentTransfers, active, seen) == seen)
                    break;
            }

            try
            {
                lock (_modelIds)
                    _modelIds.Add(modelId);
                progress?.Report(new ModelDownloadProgress(0.5, $"Downloading {modelId}"));
                await Task.Delay(10, cancellationToken);
                lock (present)
                    present.Add(modelId);
                return $"/models/{modelId}";
            }
            finally
            {
                Interlocked.Decrement(ref _activeTransfers);
            }
        }
    }

    /// <summary>Returns the first tier, then holds later transfers so readiness can be exercised mid-stage.</summary>
    private sealed class HoldAfterFirstFetch(HashSet<string> present) : IFetch
    {
        private int _calls;
        private readonly TaskCompletionSource _releaseLater = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondTransferStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void FinishLaterStages() => _releaseLater.TrySetResult();

        public async Task<string> FetchAsync(
            string modelId,
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                SecondTransferStarted.TrySetResult();
                await _releaseLater.Task.WaitAsync(cancellationToken);
            }

            lock (present)
                present.Add(modelId);
            progress?.Report(new ModelDownloadProgress(1, $"Downloaded {modelId}"));
            return $"/models/{modelId}";
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
