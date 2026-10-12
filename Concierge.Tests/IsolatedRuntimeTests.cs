using Concierge.Ai.Isolated;
using Concierge.Model.Host;
using Concierge.Shared.Chat;
using Concierge.Shared.Chat.Isolation;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Concierge.Tests;

/// <summary>
/// The model, run somewhere it is allowed to die.
///
/// The generator is native code reached through P/Invoke and it faults with an
/// access violation inside mnn_llm_generate_stream_text. That is not a managed
/// exception — by the time anything could catch it the process is already being
/// torn down — so the only way for Concierge to survive it is to not be the
/// process it happens in.
///
/// These tests use a stand-in host rather than the real model: the real one
/// needs several hundred megabytes of weights and, at time of writing, crashes.
/// What is being tested is the parent's behaviour when a child misbehaves, and
/// a stand-in that misbehaves on demand tests that better than one that only
/// crashes sometimes.
/// </summary>
public sealed class IsolatedRuntimeTests
{
    [Fact]
    public async Task Setup_completion_and_other_request_output_cannot_close_or_enter_an_active_reply()
    {
        using var runtime = new IsolatedChatRuntime();
        var reply = System.Threading.Channels.Channel.CreateUnbounded<string>();
        runtime.SetPendingReply(8, reply);
        runtime.DeliverPendingReply(new ModelHostResponse(Done: true));
        runtime.DeliverPendingReply(new ModelHostResponse(9, Chunk: "other request", Error: "other failure", Done: true));
        Assert.False(reply.Reader.Completion.IsCompleted);
        Assert.False(reply.Reader.TryRead(out _));
        runtime.DeliverPendingReply(new ModelHostResponse(8, Chunk: "correct reply", Done: true));
        Assert.True(reply.Reader.TryRead(out var text));
        Assert.Equal("correct reply", text);
        await reply.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Inference_measurements_report_sizes_and_timing_without_content()
    {
        using var runtime = new IsolatedChatRuntime();
        runtime.StartInferenceMeasurement(12, [new("system", "private instructions"), new("user", "secret")]);
        var before = runtime.LatestInference!;
        Assert.Equal(2, before.MessageCount);
        Assert.Equal(26, before.InputCharacters);
        Assert.Equal(20, before.SystemCharacters);
        Assert.Null(before.FirstChunkMilliseconds);
        Assert.Equal("waiting", before.State);
        runtime.MeasureInferenceResponse(new ModelHostResponse(99, Chunk: "wrong request", Done: true));
        Assert.Equal(before.OutputCharacters, runtime.LatestInference!.OutputCharacters);
        runtime.MeasureInferenceResponse(new ModelHostResponse(12, Chunk: "answer"));
        var first = runtime.LatestInference!.FirstChunkMilliseconds;
        Assert.NotNull(first);
        runtime.MeasureInferenceResponse(new ModelHostResponse(12, Chunk: "!", Done: true));
        var after = runtime.LatestInference!;
        Assert.Equal(first, after.FirstChunkMilliseconds);
        Assert.Equal(7, after.OutputCharacters);
        Assert.Equal("completed", after.State);
        Assert.True(after.ElapsedMilliseconds >= first);
        var json = JsonSerializer.Serialize(after);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("private instructions", json);
        Assert.DoesNotContain("answer", json);
    }

    [Fact]
    public void Failed_inference_does_not_count_the_error_as_model_output()
    {
        using var runtime = new IsolatedChatRuntime();
        runtime.StartInferenceMeasurement(9, [new("user", "Hi")]);
        runtime.MeasureInferenceResponse(new ModelHostResponse(9, Error: "Failed to load", Done: true));
        Assert.Equal("failed", runtime.LatestInference!.State);
        Assert.Equal(0, runtime.LatestInference.OutputCharacters);
        Assert.Null(runtime.LatestInference.FirstChunkMilliseconds);
    }

    [Fact]
    public void Completing_a_stream_does_not_unload_the_ready_engine()
    {
        using var runtime = new IsolatedChatRuntime();
        runtime.ApplyReadinessResponse(new ModelHostResponse(Ready: true, EngineLabel: "test engine"));
        Assert.True(runtime.IsReady);
        runtime.ApplyReadinessResponse(new ModelHostResponse(7, Done: true));
        Assert.True(runtime.IsReady);
        runtime.ApplyReadinessResponse(new ModelHostResponse(Ready: false, EngineLabel: "test engine"));
        Assert.False(runtime.IsReady);
    }

    [Fact]
    public void A_child_exit_replaces_stale_preparing_capabilities_with_unavailable()
    {
        var observedAt = DateTimeOffset.Parse("2026-10-01T04:00:00Z");
        var catalog = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.Parse("2026-10-01T03:00:00Z"),
            [
                new("conversation", "Conversation", ChatRuntimeCapabilityState.Preparing, "Loading model"),
                new("visual-input", "Understanding images", ChatRuntimeCapabilityState.Unavailable, "Not supported")
            ],
            new ChatRuntimeResourceSnapshot(8, 100, 8, "Online", null,
                DateTimeOffset.Parse("2026-10-01T03:00:00Z")),
            Activity: new ChatRuntimeActivity("model-download", "weights.bin (2/7)", 0.01));

        var updated = IsolatedChatRuntime.MarkCatalogUnavailable(
            catalog, observedAt, "Circle AI stopped while preparing. Check Concierge status before retrying.");

        Assert.Equal(observedAt, updated.ObservedAt);
        Assert.Equal(observedAt, updated.Resources?.MeasuredAt);
        Assert.Null(updated.Activity);
        Assert.Equal(ChatRuntimeCapabilityState.Unavailable, updated.Capabilities[0].State);
        Assert.Contains("stopped", updated.Capabilities[0].Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ChatRuntimeCapabilityState.Unavailable, updated.Capabilities[1].State);
    }

    // ── Finding the host ──────────────────────────────────────────────────

    /// <summary>
    /// A missing host is a deployment mistake, and it should say so. Silently
    /// looking like a model that will not load sends somebody hunting through
    /// the wrong logs.
    /// </summary>
    [Fact]
    public async Task A_missing_host_says_so_rather_than_failing_silently()
    {
        var runtime = new IsolatedChatRuntime(new IsolatedRuntimeOptions
        {
            HostPath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.exe")
        });

        var text = await CollectAsync(runtime);

        Assert.False(runtime.IsReady);
        Assert.Contains("missing", text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(runtime.CapabilityCatalog);
        Assert.All(runtime.CapabilityCatalog!.Capabilities,
            capability => Assert.Equal(ChatRuntimeCapabilityState.Unavailable, capability.State));
    }

    [Fact]
    public async Task Unsafe_worker_budget_is_exposed_as_unavailable_capabilities_with_live_memory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var runtime = new IsolatedChatRuntime(new IsolatedRuntimeOptions
        {
            HostPath = typeof(IsolatedRuntimeTests).Assembly.Location,
            WorkerMemoryBudgetProvider = () => null
        });

        await runtime.WarmUpAsync();

        Assert.False(runtime.IsReady);
        var catalog = runtime.CapabilityCatalog;
        Assert.NotNull(catalog);
        Assert.Equal(1, catalog!.SchemaVersion);
        Assert.Equal("circleai", catalog.RuntimeId);
        Assert.NotNull(catalog.Resources?.UsableMemoryGb);
        Assert.All(catalog.Capabilities, capability =>
        {
            Assert.Equal(ChatRuntimeCapabilityState.Unavailable, capability.State);
            Assert.Contains("memory", capability.Explanation, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void An_explicit_host_path_that_does_not_exist_resolves_to_nothing()
    {
        var options = new IsolatedRuntimeOptions
        {
            HostPath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.exe")
        };

        Assert.Null(options.ResolveHostPath());
    }

    [Fact]
    public void An_explicit_host_path_that_exists_is_used()
    {
        var path = Path.Combine(Path.GetTempPath(), $"host-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "not really an executable");

        try
        {
            Assert.Equal(path, new IsolatedRuntimeOptions { HostPath = path }.ResolveHostPath());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The registration replaces the in-process runtime rather than joining it.
    /// Two registered runtimes would mean the native library loaded into this
    /// process after all, which is the whole thing being avoided.
    /// </summary>
    [Fact]
    public void Registering_the_isolated_runtime_replaces_rather_than_adds()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatRuntime>(new StubRuntime());

        services.AddConciergeAiIsolated();

        using var provider = services.BuildServiceProvider();
        var runtimes = provider.GetServices<IChatRuntime>().ToList();

        Assert.Single(runtimes);
        Assert.IsType<IsolatedChatRuntime>(runtimes[0]);

        // Synchronously disposable too. A container holding a service that is
        // only IAsyncDisposable refuses Dispose(), and that lands at process
        // exit where it is least welcome.
        Assert.IsAssignableFrom<IDisposable>(runtimes[0]);
    }

    /// <summary>
    /// **And on a platform with no child processes it replaces nothing.**
    ///
    /// Found by reading what the handheld would actually do rather than by
    /// running it: an APK carries no executable beside the app, Android will not
    /// run one out of the app's own data directory, and iOS forbids child
    /// processes outright. So the isolation deleted the in-process runtime — the
    /// only one that could reach the arm64 inference library sitting in the same
    /// APK — and installed one that could never start. Every turn on the phone
    /// answered "The model host is missing from this install."
    ///
    /// Nobody decided that. It fell out of a registration written for a desktop.
    /// </summary>
    [Fact]
    public void But_where_a_child_process_is_impossible_it_leaves_the_runtime_alone()
    {
        var services = new ServiceCollection();
        var inProcess = new StubRuntime();
        services.AddSingleton<IChatRuntime>(inProcess);

        services.AddConciergeAiIsolated(childProcessesArePossible: false);

        using var provider = services.BuildServiceProvider();

        Assert.Same(inProcess, Assert.Single(provider.GetServices<IChatRuntime>()));
    }

    /// <summary>
    /// And it registers nothing at all on that platform — a hosted service that
    /// hunts for a host on every start-up is work for nobody, and a runtime in
    /// the container that cannot run is the next person's hour.
    /// </summary>
    [Fact]
    public void And_adds_nothing_of_its_own_there()
    {
        var services = new ServiceCollection();

        services.AddConciergeAiIsolated(childProcessesArePossible: false);

        Assert.Empty(services);
    }

    /// <summary>
    /// The platform answer itself, on the machine this runs on. A desktop can
    /// host a child process, which is why the tests above have to say otherwise
    /// out loud rather than waiting for somebody to run them on a phone.
    /// </summary>
    [Fact]
    public void A_desktop_can_host_one()
        => Assert.True(IsolatedAiServiceCollectionExtensions.CanHostAChildProcess);

    /// <summary>
    /// Same id as the runtime it replaces, so the provider picker, the settings
    /// panel and anything routing by id keep working.
    /// </summary>
    [Fact]
    public void It_keeps_the_identity_of_the_runtime_it_replaces()
        => Assert.Equal("circleai", new IsolatedChatRuntime().Id);

    // ── The wire ──────────────────────────────────────────────────────────

    [Fact]
    public void A_turn_round_trips_through_the_protocol()
    {
        var request = new ModelHostRequest(ModelHostProtocol.Stream, 7, new[]
        {
            new ModelHostTurn("user", "hello"),
        });

        var json = JsonSerializer.Serialize(request, ModelHostProtocol.Json);
        var back = JsonSerializer.Deserialize<ModelHostRequest>(json, ModelHostProtocol.Json)!;

        Assert.Equal(ModelHostProtocol.Stream, back.Op);
        Assert.Equal(7, back.Id);
        Assert.Equal("hello", back.Turns![0].Content);
    }

    [Fact]
    public async Task Host_reads_cancel_command_while_model_stream_is_still_running()
    {
        using var cancellation = new CancellationTokenSource();
        var streamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamRequest = new ModelHostRequest(ModelHostProtocol.Stream, 41);
        var cancelRequest = new ModelHostRequest(ModelHostProtocol.Cancel, 41);
        var input = string.Join('\n',
            JsonSerializer.Serialize(streamRequest, ModelHostProtocol.Json),
            JsonSerializer.Serialize(cancelRequest, ModelHostProtocol.Json));

        var loop = ModelHostCommandLoop.RunAsync(
            new StringReader(input),
            async _ =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    streamCancelled.TrySetResult();
                }
            },
            _ => Task.CompletedTask,
            id =>
            {
                if (id == 41)
                    cancellation.Cancel();
            },
            _ => Task.CompletedTask,
            () => { });

        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(streamCancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Host_reads_download_cancel_command_while_model_transfer_is_still_running()
    {
        using var cancellation = new CancellationTokenSource();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalls = 0;
        var input = string.Join('\n',
            JsonSerializer.Serialize(new ModelHostRequest(ModelHostProtocol.Download, 52), ModelHostProtocol.Json),
            JsonSerializer.Serialize(new ModelHostRequest(ModelHostProtocol.CancelDownload), ModelHostProtocol.Json));

        var loop = ModelHostCommandLoop.RunAsync(
            new StringReader(input),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => { },
            async _ =>
            {
                downloadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    downloadCancelled.TrySetResult();
                }
            },
            () =>
            {
                cancelCalls++;
                cancellation.Cancel();
            });

        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(downloadStarted.Task.IsCompletedSuccessfully);
        Assert.True(downloadCancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(1, cancelCalls);
    }

    [Fact]
    public async Task Host_pipe_shutdown_pauses_a_download_without_using_the_person_stop_action()
    {
        using var cancellation = new CancellationTokenSource();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var personStopCalls = 0;

        var loop = ModelHostCommandLoop.RunAsync(
            new StringReader(JsonSerializer.Serialize(
                new ModelHostRequest(ModelHostProtocol.Download, 54), ModelHostProtocol.Json)),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => { },
            async _ =>
            {
                downloadStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    downloadPaused.TrySetResult();
                }
            },
            () => personStopCalls++,
            () => cancellation.Cancel());

        await loop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(downloadStarted.Task.IsCompletedSuccessfully);
        Assert.True(downloadPaused.Task.IsCompletedSuccessfully);
        Assert.Equal(0, personStopCalls);
    }

    [Fact]
    public async Task Host_can_stop_or_reject_a_download_that_resumed_before_the_command_reader()
    {
        using var cancellation = new CancellationTokenSource();
        var resumedDownload = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // A paused runtime surfaces completion to its command owner.
            }
        });
        var personStopCalls = 0;
        var rejectedDownloads = 0;
        var input = string.Join('\n',
            JsonSerializer.Serialize(new ModelHostRequest(ModelHostProtocol.Download, 55), ModelHostProtocol.Json),
            JsonSerializer.Serialize(new ModelHostRequest(ModelHostProtocol.CancelDownload), ModelHostProtocol.Json));

        var loop = ModelHostCommandLoop.RunAsync(
            new StringReader(input),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => { },
            _ => throw new InvalidOperationException("A second transfer must not start."),
            () =>
            {
                personStopCalls++;
                cancellation.Cancel();
            },
            () => cancellation.Cancel(),
            resumedDownload,
            _ =>
            {
                rejectedDownloads++;
                return Task.CompletedTask;
            });

        await loop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, rejectedDownloads);
        Assert.Equal(1, personStopCalls);
        Assert.True(resumedDownload.IsCompleted);
    }

    [Fact]
    public async Task Host_keeps_command_reader_responsive_during_synchronous_download_startup()
    {
        var cancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupObservedCancel = false;
        var input = string.Join('\n',
            JsonSerializer.Serialize(new ModelHostRequest(ModelHostProtocol.Download, 53), ModelHostProtocol.Json),
            JsonSerializer.Serialize(new ModelHostRequest(ModelHostProtocol.CancelDownload), ModelHostProtocol.Json));

        var loop = ModelHostCommandLoop.RunAsync(
            new StringReader(input),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => { },
            _ =>
            {
                // Simulate selector and filesystem setup that blocks before the operation's
                // first await. The command reader must still be able to deliver cancellation.
                startupObservedCancel = cancelSeen.Task.Wait(TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            },
            () => cancelSeen.TrySetResult());

        await loop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(startupObservedCancel);
        Assert.True(cancelSeen.Task.IsCompletedSuccessfully);
    }

    /// <summary>
    /// The transport is a line of text, so a picture crosses it base64. Worth
    /// asserting because it is the one place in the codebase that encodes them.
    /// </summary>
    [Fact]
    public void A_picture_survives_the_crossing()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02 };

        var turn = new ModelHostTurn("user", "what is this?", new[]
        {
            new ModelHostImage("photo.jpg", "image/jpeg", Convert.ToBase64String(bytes)),
        });

        var json = JsonSerializer.Serialize(turn, ModelHostProtocol.Json);
        var back = JsonSerializer.Deserialize<ModelHostTurn>(json, ModelHostProtocol.Json)!;

        Assert.Equal(bytes, Convert.FromBase64String(back.Images![0].Base64));
        Assert.Equal("image/jpeg", back.Images![0].MediaType);
    }

    /// <summary>
    /// A response carrying no chunk and no error is a hello. Kept distinct so
    /// the parent can tell "the model loaded" from "here is a token".
    /// </summary>
    [Fact]
    public void A_hello_is_distinguishable_from_a_chunk()
    {
        var hello = JsonSerializer.Deserialize<ModelHostResponse>(
            JsonSerializer.Serialize(
                new ModelHostResponse(Ready: true, EngineLabel: "Qwen3", Status: "ready"),
                ModelHostProtocol.Json),
            ModelHostProtocol.Json)!;

        Assert.True(hello.Ready);
        Assert.Equal("Qwen3", hello.EngineLabel);
        Assert.Null(hello.Chunk);

        var chunk = JsonSerializer.Deserialize<ModelHostResponse>(
            JsonSerializer.Serialize(new ModelHostResponse(1, Chunk: "tok"), ModelHostProtocol.Json),
            ModelHostProtocol.Json)!;

        Assert.Null(chunk.EngineLabel);
        Assert.Equal("tok", chunk.Chunk);
    }

    /// <summary>
    /// Null fields are dropped on the wire. One line per chunk means the
    /// framing cost is paid per token, and every omitted null is bytes not
    /// serialised, not sent and not parsed.
    /// </summary>
    [Fact]
    public void A_chunk_carries_nothing_it_does_not_need()
    {
        var json = JsonSerializer.Serialize(new ModelHostResponse(1, Chunk: "x"), ModelHostProtocol.Json);

        Assert.DoesNotContain("engineLabel", json);
        Assert.DoesNotContain("error", json);
        Assert.DoesNotContain("null", json);
    }

    /// <summary>
    /// One object per line, so the framing holds. A serializer configured to
    /// indent would put newlines inside a message and desynchronise the reader
    /// on the very first token.
    /// </summary>
    [Fact]
    public void Nothing_on_the_wire_contains_a_newline()
    {
        var json = JsonSerializer.Serialize(
            new ModelHostResponse(1, Chunk: "a chunk with words"), ModelHostProtocol.Json);

        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
    }

    [Fact]
    public void Hello_response_preserves_the_versioned_runtime_capability_catalogue()
    {
        var catalog = new ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UnixEpoch,
            [new("visual-input", "Understanding images", ChatRuntimeCapabilityState.Unavailable, "Text-only runtime.")],
            new(3.5, 9, 6, "Online", null),
            Activity: new ChatRuntimeActivity("model-download", "Downloading model", 0.42));

        var json = JsonSerializer.Serialize(new ModelHostResponse(
            Ready: false,
            EngineLabel: "Circle AI",
            CapabilityCatalog: catalog), ModelHostProtocol.Json);
        var response = JsonSerializer.Deserialize<ModelHostResponse>(json, ModelHostProtocol.Json);

        Assert.Equal(catalog.SchemaVersion, response?.CapabilityCatalog?.SchemaVersion);
        Assert.Equal(catalog.RuntimeVersion, response?.CapabilityCatalog?.RuntimeVersion);
        Assert.Equal(ChatRuntimeCapabilityState.Unavailable, response?.CapabilityCatalog?.Capabilities[0].State);
        Assert.Equal(3.5, response?.CapabilityCatalog?.Resources?.UsableMemoryGb);
        Assert.Equal("model-download", response?.CapabilityCatalog?.Activity?.Kind);
        Assert.Equal(0.42, response?.CapabilityCatalog?.Activity?.ProgressRatio);
    }

    private static async Task<string> CollectAsync(IChatRuntime runtime)
    {
        var text = new System.Text.StringBuilder();

        await foreach (var chunk in runtime.StreamAsync(new[] { new ChatTurn("user", "hello") }))
        {
            text.Append(chunk);
        }

        return text.ToString();
    }

    private sealed class StubRuntime : IChatRuntime
    {
        public string Id => "stub";
        public string EngineLabel => "Stub";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
