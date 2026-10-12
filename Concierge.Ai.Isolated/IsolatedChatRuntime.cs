using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Concierge.Shared.Chat;
using Concierge.Shared.Chat.Isolation;
using Concierge.Shared.Sandboxing;

namespace Concierge.Ai.Isolated;

/// <summary>
/// The on-device model, run in a child process so a fault in it cannot take
/// Concierge with it.
///
/// The problem this solves is specific. The generator is native code reached
/// through P/Invoke, and it faults with an access violation inside
/// mnn_llm_generate_stream_text. An access violation is not a managed
/// exception: by the time anything could catch it the process is already
/// being torn down. No amount of try/catch around the call helps, and the
/// codebase has a comment claiming otherwise that was written before anyone
/// tried it.
///
/// So the model runs somewhere else. When it faults, that process dies, this
/// one notices a closed pipe, and the conversation gets a sentence explaining
/// what happened instead of the application disappearing mid-sentence.
///
/// What this deliberately does not do is restart the child mid-reply and try
/// again. The reply that faulted is gone, the state that produced it is gone,
/// and quietly re-running a generation that just crashed the model is how you
/// get a loop. The next message starts a fresh child.
/// </summary>
public sealed class IsolatedChatRuntime : IChatRuntime, IModelDownloadRequired, IInitializableChatRuntime, IChatRuntimeStatusEvents, IChatRuntimeCapabilitySource, IVisionCapableRuntime, IManagedHostTelemetrySource, IDisposable, IAsyncDisposable
{
    private readonly IsolatedRuntimeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _streamGate = new(1, 1);

    private Process? _process;
    private WindowsJobObjectSandbox? _hostJob;
    private long? _workerMemoryBudgetBytes;
    private long? _workerResidentMemoryBudgetBytes;
    private readonly object _workerBudgetGate = new();
    private StreamWriter? _toChild;
    private int _nextRequestId;

    private string _engineLabel = "On-device model";
    private bool _isReady;
    private bool _helloReceived;
    private string _status = "Not started yet.";
    private PendingModelDownload? _pendingDownload;
    private ModelDownloadProgress? _currentDownloadProgress;
    private ChatRuntimeCapabilityCatalog? _capabilityCatalog;
    private TaskCompletionSource<bool>? _downloadCompletion;
    private int _downloadRequestId;
    private IProgress<ModelDownloadProgress>? _downloadProgress;

    /// <summary>Set when the child has faulted, so the status says something
    /// truthful rather than "ready" about a process that is gone.</summary>
    private bool _childFaulted;

    public IsolatedChatRuntime(IsolatedRuntimeOptions? options = null)
        => _options = options ?? new IsolatedRuntimeOptions();

    // Same identity as the in-process runtime it replaces: everything that
    // routes by id — the provider picker, the settings panel — keeps working.
    public string Id => "circleai";

    /// <summary>
    /// Still on the device. The model moved to a child process so a native fault
    /// costs the sentence rather than the application — it did not move machines.
    /// </summary>
    public bool LeavesDevice => false;

    public string EngineLabel => _engineLabel;

    public bool IsReady => _isReady && !_childFaulted;

    public string StatusMessage => _status;

    public PendingModelDownload? PendingDownload => _pendingDownload;

    public ModelDownloadProgress? CurrentDownloadProgress => _currentDownloadProgress;

    public string HostId => "circleai-model";

    public string DisplayName => "Circle AI model worker";

    private volatile InferenceMeasurement? _latestInference;
    private long _inferenceStarted;
    public InferenceMeasurement? LatestInference
    {
        get
        {
            var measurement = _latestInference;
            return measurement is { State: "waiting" or "streaming" }
                ? measurement with { ElapsedMilliseconds = Stopwatch.GetElapsedTime(_inferenceStarted).TotalMilliseconds }
                : measurement;
        }
    }

    internal void StartInferenceMeasurement(int id, IReadOnlyList<ChatTurn> messages)
    {
        _inferenceStarted = Stopwatch.GetTimestamp();
        _latestInference = new(id, messages.Count,
            messages.Sum(message => (long)message.Content.Length),
            messages.Where(message => message.Role == "system").Sum(message => (long)message.Content.Length),
            DateTimeOffset.UtcNow, 0, null, 0, "waiting");
    }

    internal void MeasureInferenceResponse(ModelHostResponse response)
    {
        var measurement = _latestInference;
        if (measurement is null || measurement.RequestId != response.Id) return;
        var elapsed = Stopwatch.GetElapsedTime(_inferenceStarted).TotalMilliseconds;
        if (response.Chunk is { Length: > 0 } chunk)
            measurement = measurement with
            {
                FirstChunkMilliseconds = measurement.FirstChunkMilliseconds ?? elapsed,
                OutputCharacters = measurement.OutputCharacters + chunk.Length,
                State = "streaming"
            };
        if (response.Error is { Length: > 0 }) measurement = measurement with { State = "failed" };
        if (response.Done && measurement.State != "failed") measurement = measurement with { State = "completed" };
        _latestInference = measurement with { ElapsedMilliseconds = elapsed };
    }

    /// <summary>Current .NET model-worker memory/CPU snapshot for the Host Manager.</summary>
    public ManagedHostSnapshot? CurrentSnapshot
    {
        get
        {
            var process = _process;
            if (process is null)
                return null;
            try
            {
                process.Refresh();
                if (process.HasExited)
                    return null;
                RefreshWorkerResidentMemoryBudget(process);
                return new ManagedHostSnapshot(
                    process.Id,
                    process.WorkingSet64,
                    process.PrivateMemorySize64,
                    process.TotalProcessorTime,
                    _workerMemoryBudgetBytes,
                    DateTimeOffset.UtcNow,
                    _workerResidentMemoryBudgetBytes);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }

    public ChatRuntimeCapabilityCatalog? CapabilityCatalog
    {
        get
        {
            var catalog = _capabilityCatalog;
            if (catalog is null)
                return null;

            if (_currentDownloadProgress is { } progress)
                return catalog with
                {
                    Activity = new ChatRuntimeActivity(
                        "model-download",
                        progress.Description,
                        Math.Clamp(progress.Ratio, 0, 1))
                };

            return catalog;
        }
    }

    public IReadOnlyCollection<string> SupportedImageMediaTypes
        => _capabilityCatalog?.SupportedImageMediaTypes ?? [];

    public event EventHandler? StatusChanged;

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _streamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The host has one active generation slot. Serialize callers so a
            // second conversation cannot replace the first stream's channel or
            // cancellation identity while the child is still answering.
            var (started, requestId, chunks, failure) = await BeginAsync(messages, cancellationToken)
                .ConfigureAwait(false);

            if (!started)
            {
                yield return $"[{failure}]";
                yield break;
            }

            // Cancelling asks the child to stop rather than killing it: a model
            // that stopped when asked is still a good model to keep loaded.
            using var registration = cancellationToken.Register(() => _ = CancelAsync(requestId));

            // Read with None, not the caller's token: when the caller cancels, the
            // child still owes a Done, and the pump still owes the closing note if
            // it faulted. Tearing the channel down here would lose both.
            await foreach (var chunk in chunks!.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                yield return chunk;
            }

            // The child reports Done after acknowledging Cancel. Preserve that
            // response boundary, then rethrow cancellation so TurnRunner writes
            // a cancelled receipt and keeps any chunks as partial output.
            if (cancellationToken.IsCancellationRequested && _latestInference is { } cancelled
                && cancelled.RequestId == requestId)
                _latestInference = cancelled with { State = "cancelled" };
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _streamGate.Release();
        }
    }

    /// <summary>
    /// Starts the child if needed and posts the request. Returns why it could
    /// not rather than throwing, because every caller of this turns a failure
    /// into text for the person to read.
    /// </summary>
    private async Task<(bool Started, int RequestId, Channel<string>? Chunks, string Failure)> BeginAsync(
        IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken)
    {
        var chunks = Channel.CreateUnbounded<string>();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureChildAsync().ConfigureAwait(false))
            {
                return (false, 0, null, _status);
            }

            if (_process is { HasExited: false } runningWorker)
                RefreshWorkerResidentMemoryBudget(runningWorker);

            if (!IsReady)
                return (false, 0, null, _status);

            var requestId = ++_nextRequestId;
            SetPendingReply(requestId, chunks);
            StartInferenceMeasurement(requestId, messages);

            var request = new ModelHostRequest(
                ModelHostProtocol.Stream,
                requestId,
                messages.Select(ToWire).ToArray());

            try
            {
                await _toChild!.WriteLineAsync(JsonSerializer.Serialize(request, ModelHostProtocol.Json))
                               .ConfigureAwait(false);
                await _toChild.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The child went between the check and the write.
                MarkFaulted(_isReady);
                MeasureInferenceResponse(new ModelHostResponse(requestId, Error: "Host connection lost.", Done: true));
                _pending = null;
                return (false, 0, null, _status);
            }

            return (true, requestId, chunks, string.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Channel<string>? _pending;
    private int _pendingRequestId;

    public async Task<bool> AcceptDownloadAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Task<bool> completion;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A ready local model does not mean the staged offline upgrade is
            // finished. Circle AI may deliberately keep answering with its
            // smallest compatible model while the larger selected tier downloads.
            // Only the child's current offer decides whether there is work to do.
            if (!await EnsureChildAsync().ConfigureAwait(false) || _pendingDownload is null)
                return false;

            _downloadRequestId = ++_nextRequestId;
            _downloadCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _downloadProgress = progress;
            completion = _downloadCompletion.Task;
            await SendRequestCoreAsync(new ModelHostRequest(ModelHostProtocol.Download, _downloadRequestId)).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _status = $"Could not start the model download: {ex.Message}";
            return false;
        }
        finally
        {
            _gate.Release();
        }

        using var registration = cancellationToken.Register(() => _ = CancelDownloadAsync());
        try
        {
            return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public Task CancelCurrentDownloadAsync() => CancelDownloadAsync();

    private async Task CancelDownloadAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await SendRequestCoreAsync(new ModelHostRequest(ModelHostProtocol.CancelDownload)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // The model host may already have exited while cancellation was requested.
        }
    }

    private async Task SendRequestCoreAsync(ModelHostRequest request)
    {
        if (_toChild is null || _process is null || _process.HasExited)
            throw new IOException("The model host is not running.");
        await _toChild.WriteLineAsync(JsonSerializer.Serialize(request, ModelHostProtocol.Json)).ConfigureAwait(false);
        await _toChild.FlushAsync().ConfigureAwait(false);
    }

    private static ModelHostTurn ToWire(ChatTurn turn)
        => new(turn.Role, turn.Content,
            turn.Images?.Select(i => new ModelHostImage(i.FileName, i.MediaType, Convert.ToBase64String(i.Bytes))).ToArray());

    // ── The child ─────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the child and waits for it to say hello.
    ///
    /// Needed because the composer stays disabled until IsReady, and IsReady
    /// only becomes true once the child has loaded — so starting lazily on the
    /// first message means the first message can never be sent. The in-process
    /// runtime this replaces had a hosted service for the same reason.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCapabilityCatalogPreparing();
            await EnsureChildAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => WarmUpAsync(cancellationToken);

    /// <summary>
    /// Starts the child if it is not running. Returns false with a readable
    /// status when it cannot be started at all — a missing executable is a
    /// deployment problem, and saying so beats a silent dead composer.
    /// </summary>
    private async Task<bool> EnsureChildAsync()
    {
        if (_process is { HasExited: false })
        {
            return true;
        }

        var executable = _options.ResolveHostPath();
        if (executable is null)
        {
            SetUnavailable("The model host is missing from this install, so nothing can answer on this device.");
            return false;
        }

        ManagedHostMemoryBudget? memoryBudget = null;
        WindowsJobObjectSandbox? hostJob = null;
        string? managerGateFile = null;
        if (OperatingSystem.IsWindows())
        {
            memoryBudget = _options.WorkerMemoryBudgetProvider is { } budgetProvider
                ? budgetProvider()
                : ManagedHostMemoryBudget.MeasureWindows();
            if (memoryBudget is null)
            {
                var measuredMemory = ManagedHostMemoryBudget.MeasureWindowsSnapshot();
                var explanation = measuredMemory is { } snapshot
                    ? "Circle AI is paused on Desktop because it does not have enough free memory to safely start its worker. Close some apps and try again."
                    : "Circle AI is paused on Desktop because Windows could not measure free memory safely. Close some apps and try again.";
                var resources = measuredMemory is { } current
                    ? new ChatRuntimeResourceSnapshot(
                        current.AvailableBytes / (1024d * 1024 * 1024),
                        null,
                        Environment.ProcessorCount,
                        null,
                        "Free physical memory is measured; no safe worker budget is currently available.",
                        DateTimeOffset.UtcNow)
                    : null;
                SetUnavailable(explanation, resources);
                return false;
            }

            hostJob = new WindowsJobObjectSandbox(
                memoryCapBytes: memoryBudget.WorkerLimitBytes,
                processCap: 1);
            try
            {
                hostJob.OpenJob();
            }
            catch (Exception ex)
            {
                DisposeHostJob(hostJob);
                SetUnavailable($"Circle AI could not start its managed worker: {ex.Message}");
                return false;
            }

            // The worker waits at its entry point until it has been assigned to
            // the job. This closes the startup window in which an over-budget
            // model load could run before the OS limits were attached.
            managerGateFile = Path.Combine(Path.GetTempPath(), $"concierge-host-{Guid.NewGuid():N}.gate");
            try
            {
                File.WriteAllText(managerGateFile, string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DisposeHostJob(hostJob);
                SetUnavailable($"Circle AI could not prepare its managed worker: {ex.Message}");
                return false;
            }
        }

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };

        if (memoryBudget is not null)
            start.Environment["CONCIERGE_HOST_MEMORY_BUDGET_BYTES"] = memoryBudget.WorkerLimitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (managerGateFile is not null)
            start.Environment["CONCIERGE_HOST_MANAGER_GATE_FILE"] = managerGateFile;

        try
        {
            _process = Process.Start(start);
            if (_process is null)
                throw new InvalidOperationException("The model host would not start.");

            if (OperatingSystem.IsWindows() && hostJob is not null)
            {
                hostJob.Confine(_process);
                WindowsProcessMemoryLimiter.Apply(_process, memoryBudget!.WorkerLimitBytes);
            }

            DisposeHostJob(_hostJob);
            _hostJob = hostJob;
            hostJob = null;
            _workerMemoryBudgetBytes = memoryBudget?.WorkerLimitBytes;
            _workerResidentMemoryBudgetBytes = memoryBudget?.WorkerLimitBytes;
            _toChild = _process.StandardInput;
            if (managerGateFile is not null)
                File.Delete(managerGateFile);
            try { _process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        catch (Exception ex)
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception) { }
            _process?.Dispose();
            _process = null;
            _toChild = null;
            DisposeHostJob(_hostJob);
            _hostJob = null;
            _workerMemoryBudgetBytes = null;
            _workerResidentMemoryBudgetBytes = null;
            DisposeHostJob(hostJob);
            try
            {
                if (managerGateFile is not null && File.Exists(managerGateFile))
                    File.Delete(managerGateFile);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            SetUnavailable($"The model host would not start: {ex.Message}");
            return false;
        }

        _childFaulted = false;

        // Read the child's stdout on its own loop for the life of the process.
        _ = Task.Run(() => PumpAsync(_process));

        // And drain stderr, which is redirected and would otherwise fill.
        // A redirected pipe nobody reads blocks the writer once the buffer is
        // full — the child would wedge partway through loading, which looks
        // exactly like a model that is slow rather than one that is stuck.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                    // Diagnostics from the child. Read and dropped: the parent
                    // has no log to put them in, and the point is to keep the
                    // pipe empty.
                }
            }
            catch (Exception)
            {
                // The process went. The stdout pump handles that.
            }
        });

        // The hello carries the label and whether the model loaded. Bounded:
        // a child that never says hello must not hang the composer forever.
        var deadline = DateTimeOffset.UtcNow + _options.StartTimeout;
        while (!_helloReceived && !_childFaulted && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        if (!_helloReceived && !_childFaulted)
        {
            SetUnavailable("The model did not finish loading in time. Restart Concierge to retry its managed worker.");
        }

        if (_isReady)
        {
            // A child that came up healthy earns the budget back, so a fault an
            // hour ago does not count against one tonight.
            _restarts = 0;
        }

        // The child may be alive and waiting for download approval. It is
        // initialized, even though it cannot answer until the model is ready.
        return _helloReceived && _process is { HasExited: false };
    }

    private void RefreshWorkerResidentMemoryBudget(Process process)
    {
        if (!OperatingSystem.IsWindows() || _workerMemoryBudgetBytes is not { } startupLimit)
            return;

        lock (_workerBudgetGate)
        {
            var liveBudget = _options.WorkerMemoryBudgetProvider is { } budgetProvider
                ? budgetProvider()
                : ManagedHostMemoryBudget.MeasureWindows(process);
            if (liveBudget is null)
                return;

            try
            {
                process.Refresh();
                if (process.HasExited)
                    return;

                var currentLimit = _workerResidentMemoryBudgetBytes ?? startupLimit;
                _workerResidentMemoryBudgetBytes = ManagedHostMemoryBudget.TightenAndApplyResidentLimit(
                    currentLimit,
                    startupLimit,
                    liveBudget.WorkerLimitBytes,
                    process);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentOutOfRangeException)
            {
                // Keep the last successfully enforced value visible. The configured commit
                // ceiling remains in force; a later live sample can retry the resident cap.
            }
        }
    }

    /// <summary>
    /// Reads the child until it stops. Ending is not an error path — it is the
    /// case this class exists for, and it is handled here rather than thrown.
    /// </summary>
    private async Task PumpAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                ModelHostResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<ModelHostResponse>(line, ModelHostProtocol.Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (response is null)
                {
                    continue;
                }

                if (response.EngineLabel is not null)
                {
                    _engineLabel = response.EngineLabel;
                    _helloReceived = true;
                }

                if (response.Status is not null)
                    _status = response.Status;
                if (response.CapabilityCatalog is not null)
                    _capabilityCatalog = response.CapabilityCatalog;
                if (response.PendingDownload is not null || response.Ready)
                    _pendingDownload = response.PendingDownload;
                ApplyReadinessResponse(response);
                MeasureInferenceResponse(response);
                if (response.ProgressRatio is { } ratio)
                {
                    var downloadProgress = new ModelDownloadProgress(ratio, response.ProgressDescription ?? _status);
                    _currentDownloadProgress = downloadProgress;
                    _downloadProgress?.Report(downloadProgress);
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }

                if (response.Done && (response.Id == 0 || response.Id == _downloadRequestId))
                {
                    if (response.Id != 0)
                    {
                        _downloadCompletion?.TrySetResult(response.Ready);
                        _downloadCompletion = null;
                        _downloadProgress = null;
                    }
                    _currentDownloadProgress = null;
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                }

                if (response.Status is not null || response.Ready || response.Error is not null || response.CapabilityCatalog is not null)
                    StatusChanged?.Invoke(this, EventArgs.Empty);

                DeliverPendingReply(response);
            }
        }
        catch (Exception)
        {
            // Reading failed because the process went. Same handling as a
            // clean end of stream.
        }

        // Out of the loop means the child is gone. If a reply was in flight,
        // it faulted — say so in the conversation rather than leaving the
        // composer spinning on a stream that will never produce another token.
        if (_pending is { } waiting)
        {
            if (_latestInference is { } activeInference)
                MeasureInferenceResponse(new ModelHostResponse(activeInference.RequestId, Error: "Host stopped.", Done: true));
            var wasReady = _isReady;
            MarkFaulted(wasReady);
            waiting.Writer.TryWrite(
                "\n\n[The model stopped unexpectedly. Concierge is still running — send the message again to try once more.]");
            waiting.Writer.TryComplete();
            _pending = null;
        }
        else if (process.HasExited)
        {
            // A clean exit is still an unexpected loss of the runtime: the
            // parent did not ask the host to stop. Do not leave the last
            // Preparing catalog published to remote devices indefinitely.
            MarkFaulted(_isReady);
        }

        if (ReferenceEquals(_process, process))
        {
            DisposeHostJob(_hostJob);
            _hostJob = null;
            _workerMemoryBudgetBytes = null;
            _workerResidentMemoryBudgetBytes = null;
        }

        _downloadCompletion?.TrySetResult(false);
        _downloadCompletion = null;
        _downloadProgress = null;
    }

    /// <summary>How many times the child has been restarted after a fault.
    /// Capped, because a model that faults while loading would otherwise be
    /// restarted forever.</summary>
    private int _restarts;

    private const int MaxRestarts = 3;

    internal void ApplyReadinessResponse(ModelHostResponse response)
    {
        // Stream completion carries no runtime state. Its default Ready=false
        // must not advertise a loaded engine as unavailable between turns.
        if (response.EngineLabel is not null
            || response.Done && (response.Id == 0 || response.Id == _downloadRequestId))
            _isReady = response.Ready;
    }

    internal void SetPendingReply(int requestId, Channel<string> reply)
    {
        _pendingRequestId = requestId;
        _pending = reply;
    }

    internal void DeliverPendingReply(ModelHostResponse response)
    {
        // Download/setup completions share this pipe with inference. Only the
        // matching request may deliver output or close its conversation's stream.
        if (_pending is not { } reply || response.Id != _pendingRequestId) return;
        if (response.Chunk is { Length: > 0 }) reply.Writer.TryWrite(response.Chunk);
        if (response.Error is { Length: > 0 }) reply.Writer.TryWrite($"[{response.Error}]");
        if (response.Done)
        {
            reply.Writer.TryComplete();
            _pending = null;
            _pendingRequestId = 0;
        }
    }

    private void MarkFaulted(bool wasReady)
    {
        _childFaulted = true;
        _isReady = false;
        // A stopped host cannot still be downloading. Drop both the live
        // overlay and any activity captured in its last capability snapshot
        // so Desktop and connected clients do not display frozen progress.
        _currentDownloadProgress = null;
        _downloadProgress = null;
        _downloadCompletion?.TrySetResult(false);
        _downloadCompletion = null;
        _status = wasReady
            ? "The model stopped unexpectedly."
            : "Circle AI could not finish starting.";
        if (_capabilityCatalog is { } catalog)
            _capabilityCatalog = MarkCatalogUnavailable(
                catalog,
                DateTimeOffset.UtcNow,
                wasReady
                    ? "Circle AI stopped while preparing. It will try once more automatically."
                    : "Circle AI stopped while preparing and will not retry this startup. Check Concierge status before retrying.");
        else
            _capabilityCatalog = CreateCapabilityCatalog(
                ChatRuntimeCapabilityState.Unavailable,
                _status,
                resources: null);

        // A host that dies before it has ever become ready is failing during
        // initialization (including native model loading). Restarting the same
        // deterministic failure several times burns CPU and shows false activity.
        // Let the person see the unavailable state; a model that faults during a
        // real turn can still receive the bounded recovery attempt below.
        if (!wasReady)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Restart it, rather than waiting to be asked.
        //
        // The composer is disabled while IsReady is false, so "send another
        // message and it will start again" is advice nobody can follow — the
        // control that would send it is greyed out. Without this the app
        // survives the fault and is then useless until it is restarted, which
        // is only half the point.
        //
        // Capped and not looped: a model that faults during loading would
        // otherwise be restarted until the machine gave up.
        if (_restarts >= MaxRestarts)
        {
            _status = "The model stopped repeatedly, so it has not been started again. Restart Concierge to try once more.";
            if (_capabilityCatalog is { } failedCatalog)
                _capabilityCatalog = MarkCatalogUnavailable(
                    failedCatalog,
                    DateTimeOffset.UtcNow,
                    "Circle AI stopped repeatedly while preparing. Check Concierge status before retrying.");
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _restarts++;
        StatusChanged?.Invoke(this, EventArgs.Empty);

        _ = Task.Run(async () =>
        {
            // A moment first: restarting instantly into whatever just faulted
            // tends to fault again.
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            try
            {
                await WarmUpAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // EnsureChildAsync records why in _status.
            }
        });
    }

    internal static ChatRuntimeCapabilityCatalog MarkCatalogUnavailable(
        ChatRuntimeCapabilityCatalog catalog, DateTimeOffset observedAt, string explanation)
        => catalog with
        {
            ObservedAt = observedAt,
            Activity = null,
            Capabilities = catalog.Capabilities.Select(capability =>
                capability.State == ChatRuntimeCapabilityState.Preparing
                    ? capability with
                    {
                        State = ChatRuntimeCapabilityState.Unavailable,
                        Explanation = explanation
                    }
                    : capability).ToArray(),
            Resources = catalog.Resources is { } resources
                ? resources with { MeasuredAt = observedAt }
                : null
        };

    private void EnsureCapabilityCatalogPreparing()
    {
        _capabilityCatalog ??= CreateCapabilityCatalog(
            ChatRuntimeCapabilityState.Preparing,
            "Circle AI is checking this Desktop's resources and starting its managed worker.",
            resources: null);
    }

    private void SetUnavailable(string explanation, ChatRuntimeResourceSnapshot? resources = null)
    {
        _status = explanation;
        _isReady = false;
        _capabilityCatalog = CreateCapabilityCatalog(
            ChatRuntimeCapabilityState.Unavailable,
            explanation,
            resources);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ChatRuntimeCapabilityCatalog CreateCapabilityCatalog(
        ChatRuntimeCapabilityState state,
        string explanation,
        ChatRuntimeResourceSnapshot? resources)
        => new(
            SchemaVersion: 1,
            RuntimeId: "circleai",
            // The isolated parent intentionally does not reference Circle AI's
            // inference assemblies. Until its child reports a version, be honest.
            RuntimeVersion: "unreported",
            ObservedAt: DateTimeOffset.UtcNow,
            Capabilities:
            [
                new("conversation", "Conversation", state, explanation),
                new("tool-use", "Working with tools", state, explanation),
                new("visual-input", "Understanding images", state, explanation),
                new("reasoning", "Careful reasoning", state, explanation),
                new("long-context", "Longer conversations", state, explanation)
            ],
            Resources: resources);

    private async Task CancelAsync(int requestId)
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await SendRequestCoreAsync(new ModelHostRequest(ModelHostProtocol.Cancel, requestId)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // Cancelling a child that has already gone is not worth reporting.
        }
    }

    /// <summary>
    /// Both, deliberately. A service registered only as IAsyncDisposable makes
    /// the DI container refuse to be disposed synchronously — which is how a
    /// host may well shut down, and the failure arrives at process exit where
    /// it is least welcome. Stopping a child process needs no await anyway.
    /// </summary>
    public void Dispose() => Stop();

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                // Closing stdin asks it to stop; killing is the fallback for
                // one that is wedged inside native code and will not read.
                _toChild?.Close();

                if (!_process.WaitForExit(2000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception)
        {
            // Shutting down. Nothing here is worth failing over.
        }

        _process?.Dispose();
        DisposeHostJob(_hostJob);
        _hostJob = null;
        _workerMemoryBudgetBytes = null;
        _workerResidentMemoryBudgetBytes = null;
        _gate.Dispose();
    }

    private static void DisposeHostJob(WindowsJobObjectSandbox? job)
    {
        if (OperatingSystem.IsWindows())
            job?.Dispose();
    }
}
