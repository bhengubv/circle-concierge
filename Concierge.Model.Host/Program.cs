using System.Text.Json;
using Concierge.Ai;
using Concierge.Shared.Chat;
using Concierge.Shared.Chat.Isolation;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Model.Host;

/// <summary>
/// Owns the model, and is allowed to die.
///
/// Reads one JSON request per line on stdin and writes one JSON response per
/// line on stdout. Nothing else goes to stdout — every diagnostic goes to
/// stderr — because a stray Console.WriteLine here is a parse error in the
/// parent.
///
/// There is no crash handling in this file, deliberately. An access violation
/// in the native generator cannot be caught in managed code, and trying would
/// only add a layer that fails to. The parent watches the pipe instead: when
/// this process goes, the pipe closes, and that is the signal.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan CapabilityRefreshInterval = TimeSpan.FromSeconds(15);
    private static CircleAiChatRuntime? _runtime;
    private static CancellationTokenSource? _current;
    private static int _currentRequestId;
    private static CancellationTokenSource? _downloadCancellation;
    private static readonly SemaphoreSlim OutputGate = new(1, 1);
    private static CoalescingProgressReporter<ModelHostResponse>? _runtimeStateOutput;

    private static async Task<int> Main()
    {
        try
        {
            await WaitForHostManagerAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"The model host could not enter its managed process boundary: {ex.Message}");
            return 2;
        }

        // stdin and stdout carry the protocol. Anything the runtime logs must
        // not land in the middle of it.
        var output = Console.Out;
        Console.SetOut(Console.Error);
        using var capabilityUpdates = new CancellationTokenSource();
        Task? capabilityPublisher = null;
        Task? acceptedDownloadResume = null;

        try
        {
            _runtime = Build();

            // Tell the parent that the host is alive before loading starts.
            // Loading an existing model can take minutes; withholding the first
            // response leaves both clients unable to show meaningful activity.
            await WriteRuntimeStateAsync(output, _runtime);
            var runtimeStateOutput = new CoalescingProgressReporter<ModelHostResponse>(
                response => WriteAsync(output, response));
            _runtimeStateOutput = runtimeStateOutput;
            _runtime.StatusChanged += (_, _) => runtimeStateOutput.Report(CreateRuntimeStateResponse(_runtime));
            capabilityPublisher = PublishCapabilityUpdatesAsync(output, capabilityUpdates.Token);

            // Load it here rather than waiting for something else to.
            //
            // AddConciergeAi registers a hosted service that calls LoadAsync,
            // and nothing in this process runs hosted services — so without
            // this the runtime sits at "Engine queued for load" forever and the
            // parent is told, correctly, that nothing is ready. Confirmed by
            // running this executable by hand.
            await _runtime.LoadAsync(CancellationToken.None);

            await WriteRuntimeStateAsync(output, _runtime);

            // Resume only a download whose exact model plan was accepted earlier. Run it
            // beside the command reader so the parent can attach, show progress, or stop it.
            acceptedDownloadResume = Task.Run(async () =>
            {
                ModelHostResponse completion;
                await using (var progress = new CoalescingProgressReporter<ModelDownloadProgress>(report =>
                    WriteAsync(output, new ModelHostResponse(
                        ProgressRatio: report.Ratio,
                        ProgressDescription: report.Description))))
                {
                    try
                    {
                        await _runtime.ResumeAcceptedDownloadAsync(progress).ConfigureAwait(false);
                        completion = CreateRuntimeStateResponse(_runtime) with { Id = 0, Done = true };
                    }
                    catch (Exception ex)
                    {
                        completion = CreateRuntimeStateResponse(_runtime) with
                        {
                            Id = 0,
                            Done = true,
                            Status = $"Could not resume the accepted Circle AI download: {ex.Message}",
                        };
                    }
                }
                await WriteAsync(output, completion).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            // Failing to load is not the crash this design is about, and the
            // parent can show it. Report and exit cleanly.
            await WriteAsync(output, new ModelHostResponse(
                Ready: false,
                EngineLabel: "unavailable",
                Status: $"The model host could not start: {ex.Message}"));
            return 1;
        }

        await ModelHostCommandLoop.RunAsync(
            Console.In,
            request => StreamAsync(output, request),
            request => WriteAsync(output, new ModelHostResponse(
                request.Id, Error: "Another model response is already running.", Done: true)),
            requestId =>
            {
                if (_currentRequestId == requestId)
                    _current?.Cancel();
            },
            request => DownloadAsync(output, request.Id),
            CancelAcceptedDownload,
            PauseAcceptedDownload,
            acceptedDownloadResume,
            request => WriteAsync(output, new ModelHostResponse(
                Id: request.Id,
                Done: true,
                Ready: _runtime.IsReady,
                EngineLabel: _runtime.EngineLabel,
                Status: _runtime.StatusMessage,
                PendingDownload: _runtime.PendingDownload,
                CapabilityCatalog: _runtime.CapabilityCatalog)));

        if (acceptedDownloadResume is not null)
            await acceptedDownloadResume.ConfigureAwait(false);

        capabilityUpdates.Cancel();
        if (capabilityPublisher is not null)
        {
            try
            {
                await capabilityPublisher;
            }
            catch (OperationCanceledException)
            {
                // Expected when the parent closes the protocol pipe.
            }
        }

        if (_runtimeStateOutput is not null)
        {
            await _runtimeStateOutput.DisposeAsync().ConfigureAwait(false);
            _runtimeStateOutput = null;
        }

        // stdin closed: the parent has gone, so this should too rather than
        // linger holding a model in memory.
        return 0;
    }

    private static async Task WaitForHostManagerAsync()
    {
        var gateFile = Environment.GetEnvironmentVariable("CONCIERGE_HOST_MANAGER_GATE_FILE");
        if (string.IsNullOrWhiteSpace(gateFile))
            return;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (File.Exists(gateFile))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Concierge did not apply the worker's process budget in time.");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static CircleAiChatRuntime Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The prefix cache, switchable from outside for the length of one run.
        //
        // It is off by default and the reason is written at the call site: the native
        // generate path faulted whenever a system turn was present, which is every real
        // turn, and a bare host run by hand sends none — so it looked healthy every time
        // anybody checked. The comment there ends "turn it back on when the native cache
        // path is fixed, and re-run the three payloads before believing it."
        //
        // This is how those three get re-run without editing a default and forgetting to
        // put it back. Nothing reads it in normal use, and the default it overrides is
        // still the careful one.
        services.AddConciergeAi(new CircleAiChatOptions
        {
            // The parent Concierge surface must receive the selected bundle size and ask
            // before sending the Download command; the isolated host never auto-fetches.
            AllowAutomaticDownload = false,
            StageEligibleModelsSmallestFirst = true,
            UsePrefixCache = string.Equals(
                Environment.GetEnvironmentVariable("CONCIERGE_PREFIX_CACHE"), "1", StringComparison.Ordinal),
        });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<CircleAiChatRuntime>();
    }

    private static async Task StreamAsync(TextWriter output, ModelHostRequest request)
    {
        _current?.Dispose();
        _current = new CancellationTokenSource();
        _currentRequestId = request.Id;
        var token = _current.Token;

        var turns = (request.Turns ?? Array.Empty<ModelHostTurn>())
            .Select(t => new ChatTurn(
                t.Role,
                t.Content,
                t.Images?.Select(i => new ChatImage(i.FileName, i.MediaType, Convert.FromBase64String(i.Base64))).ToArray()))
            .ToList();

        try
        {
            await foreach (var chunk in _runtime!.StreamAsync(turns, token))
            {
                await WriteAsync(output, new ModelHostResponse(request.Id, Chunk: chunk));
            }

            await WriteAsync(output, new ModelHostResponse(request.Id, Done: true));
        }
        catch (OperationCanceledException)
        {
            await WriteAsync(output, new ModelHostResponse(request.Id, Done: true));
        }
        catch (Exception ex)
        {
            // A managed failure the parent can show. The unmanaged kind never
            // reaches here — that is what the closed pipe is for.
            await WriteAsync(output, new ModelHostResponse(request.Id, Error: ex.Message, Done: true));
        }
        finally
        {
            if (_currentRequestId == request.Id)
            {
                _currentRequestId = 0;
                _current.Dispose();
                _current = null;
            }
        }
    }

    private static async Task DownloadAsync(TextWriter output, int requestId)
    {
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        var token = _downloadCancellation.Token;
        ModelHostResponse completion;
        await using (var progress = new CoalescingProgressReporter<ModelDownloadProgress>(report =>
            WriteAsync(output, new ModelHostResponse(requestId,
                ProgressRatio: report.Ratio,
                ProgressDescription: report.Description))))
        {
            try
            {
                await _runtime!.AcceptDownloadAsync(progress, token).ConfigureAwait(false);
                completion = CreateRuntimeStateResponse(_runtime) with { Id = requestId, Done = true };
            }
            catch (OperationCanceledException)
            {
                completion = CreateRuntimeStateResponse(_runtime!) with { Id = requestId, Done = true };
            }
            catch (Exception ex)
            {
                completion = CreateRuntimeStateResponse(_runtime!) with
                {
                    Id = requestId,
                    Done = true,
                    Status = $"Model download failed: {ex.Message}",
                };
            }
        }
        await WriteAsync(output, completion).ConfigureAwait(false);
    }

    private static void CancelAcceptedDownload()
    {
        _downloadCancellation?.Cancel();
        if (_runtime is not null)
            _runtime.CancelCurrentDownloadAsync().GetAwaiter().GetResult();
    }

    private static void PauseAcceptedDownload()
    {
        _downloadCancellation?.Cancel();
        if (_runtime is not null)
            _ = _runtime.PauseCurrentDownloadAsync();
    }

    private static async Task PublishCapabilityUpdatesAsync(TextWriter output, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CapabilityRefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var runtime = _runtime;
            if (runtime is null)
                continue;

            var catalog = runtime.RefreshCapabilityCatalog();
            try
            {
                await WriteAsync(output, new ModelHostResponse(CapabilityCatalog: catalog));
            }
            catch (IOException)
            {
                return;
            }
        }
    }

    private static Task WriteRuntimeStateAsync(TextWriter output, CircleAiChatRuntime runtime)
        => WriteAsync(output, CreateRuntimeStateResponse(runtime));

    private static ModelHostResponse CreateRuntimeStateResponse(CircleAiChatRuntime runtime)
        => new(
            Ready: runtime.IsReady,
            EngineLabel: runtime.EngineLabel,
            Status: runtime.StatusMessage,
            PendingDownload: runtime.PendingDownload,
            CapabilityCatalog: runtime.CapabilityCatalog);

    private static async Task WriteAsync(TextWriter output, ModelHostResponse response)
    {
        await OutputGate.WaitAsync();
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(response, ModelHostProtocol.Json));
            await output.FlushAsync();
        }
        finally
        {
            OutputGate.Release();
        }
    }

}
