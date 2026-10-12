using System.Diagnostics;
using System.Runtime.CompilerServices;
using CircleAI.Core;
using CircleAI.Inference;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging;

namespace Concierge.Ai;

/// <summary>
/// <see cref="IChatRuntime"/> backed by CircleAI's on-device chat generator (MNN-LLM
/// running a Qwen / Kimi family model). Model selection is device-driven through CircleAI's
/// mode-aware catalogue assessor and selector — neither the host nor the UI picks; the best
/// compatible tier for the device's current resources and MNN mode is selected.
/// </summary>
/// <remarks>
/// <para>
/// The model load is potentially slow (download + native open + warm-up). A background
/// <see cref="CircleAiChatRuntimeLoader"/> hosted service kicks it off at app start and
/// drives <see cref="IsReady"/> / <see cref="StatusMessage"/>. Callers that hit
/// <see cref="StreamAsync"/> before the load completes block on the same task — they
/// don't race the loader.
/// </para>
/// <para>
/// CircleAI 3.x mobile features wired here:
/// <list type="bullet">
///   <item><description><b>PowerBudget</b> on every generation. Defaults to
///     <see cref="PowerBudget.Normal"/>, which the runtime auto-downgrades to
///     <see cref="PowerBudget.Low"/> when the device is below 15% battery — caps
///     output tokens at ~64, prefers TQ4 KV compression, prefers a smaller model
///     in a fallback chain. Concierge MAUI on Android is the exact use case.
///   </description></item>
///   <item><description><b>PrefixCache</b> (RT-06). The runtime snapshots the
///     model's KV state once per (modelId, systemPrompt) pair so subsequent
///     conversations skip the 2-3s prefill cost. Critical for the voice loop's
///     sub-200ms first-token target.
///   </description></item>
///   <item><description><b>SaveSessionAsync / LoadSessionAsync</b> via
///     <see cref="IPersistableChatRuntime"/>. The MAUI host hooks <c>OnSleep</c>
///     and <c>OnResume</c> to survive Android OOM kills.
///   </description></item>
///   <item><description><b>Brownout</b> is covered transparently by PowerBudget's
///     <c>PreferSmallerModelInChain</c> field — when a fallback chain is wired the
///     runtime hot-swaps to the smaller model on low-RAM signals. No additional
///     code here.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class CircleAiChatRuntime : IChatRuntime, IPersistableChatRuntime, IModelDownloadRequired, IModelDownloadApprovalPreparer, IResumableModelDownload, IInitializableChatRuntime, IChatRuntimeStatusEvents, IChatRuntimeCapabilitySource, IVisionCapableRuntime, IAsyncDisposable
{
    private readonly ILogger<CircleAiChatRuntime> _logger;
    private readonly CircleAiChatOptions _options;
    private readonly ModelDownloadApprovalStore _downloadApprovalStore;

    // Everything below is guarded by _statusGate. _selected in particular is written on a pool
    // thread by LoadAsync and read on the render thread through PendingDownload.
    private ModelSelection? _selected;
    private IReadOnlyList<ModelSelection> _stagingPlan = [];
    private HashSet<string> _selectedModelCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private DeviceProbe? _deviceProbe;
    private DateTimeOffset _resourcesMeasuredAt;
    private IChatGenerator? _generator;
    private string? _activeModelId;
    private string? _activeModelPath;
    private ModelDownloadProgress? _currentDownloadProgress;
    private string? _downloadRecoveryMessage;
    private long _lastProgressStatusTimestamp;
    private bool _accepting;
    private bool _disposed;
    private bool _loadStarted;
    private CancellationTokenSource? _downloadCancellation;
    private ModelDownloadApproval? _resumeApproval;
    private bool _explicitlyStopped;

    // Cancelled on disposal. A 21 GB transfer must not outlive the app that started it, and
    // the native handle must not be freed underneath a decode that is still running.
    private readonly CancellationTokenSource _lifetime = new();

    // Not readonly: completing it with null is how "no model" is reported to waiters, and
    // accepting a download afterwards has to give later callers something to wait on again.
    private TaskCompletionSource<IChatGenerator?> _generatorReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _statusGate = new();
    // Single in-flight generation at a time — MNN model handles are not
    // re-entrant, and the Razor UI never opens more than one stream anyway.
    // SaveSessionAsync / LoadSessionAsync also acquire this so a snapshot
    // never races with a partial decode.
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private string _statusMessage = "Engine queued for load…";
    private string _engineLabel = "CircleAI (pending)";
    private bool _isReady;

    public event EventHandler? StatusChanged;

    public ModelDownloadProgress? CurrentDownloadProgress
    {
        get { lock (_statusGate) { return _currentDownloadProgress; } }
    }

    public CircleAiChatRuntime(ILogger<CircleAiChatRuntime> logger, CircleAiChatOptions options)
    {
        _logger = logger;
        _options = options;
        _downloadApprovalStore = new ModelDownloadApprovalStore(options.ModelsDirectory);
    }

    /// <summary>
    /// What selection picked, once <see cref="LoadAsync"/> has run. Null before that.
    /// Non-null with <c>RequiresDownload</c> means the engine is waiting on a decision.
    /// </summary>
    public ModelSelection? Selected
    {
        get { lock (_statusGate) { return _selected; } }
    }

    /// <inheritdoc/>
    public PendingModelDownload? PendingDownload
    {
        get
        {
            if (_options.AllowAutomaticDownload)
                return null;

            ModelSelection? selection;
            IReadOnlyList<ModelSelection> stagingPlan;
            lock (_statusGate)
            {
                selection = _selected;
                stagingPlan = _stagingPlan;
            }

            if (selection is null || selection.Quality == SelectionQuality.NothingFits)
                return null;

            var remaining = _options.StageEligibleModelsSmallestFirst
                ? stagingPlan
                    .Where(candidate => !IsAlreadyDownloaded(candidate.ModelId, _options.ModelsDirectory))
                    .ToArray()
                : [selection];
            return new PendingModelDownload(
                selection.ModelId,
                selection.EstimatedBytes,
                // NothingFits means the catalogue had nothing this device can run and the
                // smallest entry was handed back anyway. Worth saying before somebody
                // spends an hour on it.
                FitsThisDevice: selection.Quality != SelectionQuality.NothingFits)
            {
                StagingBytes = _options.StageEligibleModelsSmallestFirst
                    ? Math.Max(selection.EstimatedBytes, remaining.Sum(candidate => candidate.EstimatedBytes))
                    : selection.EstimatedBytes,
                StagedModelCount = Math.Max(1, remaining.Length),
            };
        }
    }

    public string Id => "circleai";

    /// <summary>The whole point of it. Nothing here goes anywhere.</summary>
    public bool LeavesDevice => false;
    public string EngineLabel => _engineLabel;
    public bool IsReady => _isReady;
    public string StatusMessage => _statusMessage;
    public string? SessionSnapshotPath => _options.SessionSnapshotPath;

    public ChatRuntimeCapabilityCatalog CapabilityCatalog
    {
        get
        {
            lock (_statusGate)
            {
                var cannotFit = _selected?.Quality == SelectionQuality.NothingFits;
                var usable = _isReady;
                var waiting = !_isReady && !cannotFit && IsPreparingStatus(_statusMessage, _accepting);
                var needsSetup = !_isReady && !cannotFit && _selected is not null && !_accepting;
                var visionMediaTypes = SupportedImageMediaTypes.ToArray();
                var vision = visionMediaTypes.Length > 0;
                var visionDeclared = _selectedModelCapabilities.Contains(nameof(ChatCapability.Vision));
                var toolsDeclared = _selectedModelCapabilities.Contains(nameof(ChatCapability.Tools));
                var reasoningDeclared = _selectedModelCapabilities.Contains(nameof(ChatCapability.Reasoning));
                var longContextDeclared = _selectedModelCapabilities.Contains(nameof(ChatCapability.LongContext));
                var resources = _deviceProbe is null ? null : new ChatRuntimeResourceSnapshot(
                    _deviceProbe.UsableRamGb,
                    _deviceProbe.StorageFreeGb,
                    _deviceProbe.CpuCores,
                    _deviceProbe.Connectivity.ToString(),
                    _deviceProbe.MeasurementWarning,
                    _resourcesMeasuredAt);

                var chatState = usable ? ChatRuntimeCapabilityState.Available
                    : cannotFit ? ChatRuntimeCapabilityState.Unavailable
                    : waiting ? ChatRuntimeCapabilityState.Preparing
                    : needsSetup ? ChatRuntimeCapabilityState.ActionRequired
                    : ChatRuntimeCapabilityState.Unavailable;
                var toolState = toolsDeclared ? chatState
                    : _selected is null && waiting ? ChatRuntimeCapabilityState.Preparing
                    : ChatRuntimeCapabilityState.Unavailable;

                return new ChatRuntimeCapabilityCatalog(
                    SchemaVersion: 1,
                    RuntimeId: Id,
                    RuntimeVersion: typeof(DeviceProbe).Assembly.GetName().Version?.ToString(3) ?? "unknown",
                    ObservedAt: DateTimeOffset.UtcNow,
                    Capabilities:
                    [
                        new("conversation", "Conversation", chatState,
                            usable ? null : cannotFit ? "No model fits this device's available resources." : _statusMessage),
                        new("tool-use", "Working with tools", toolState,
                            toolState == ChatRuntimeCapabilityState.Unavailable
                                ? "The active model has not been confirmed for tool use."
                                : usable ? null : cannotFit ? "This device cannot prepare a model for tool use." : _statusMessage),
                        new("visual-input", "Understanding images", vision ? ChatRuntimeCapabilityState.Available
                            : !usable && visionDeclared ? OptionalFeatureState(visionDeclared, usable, waiting, needsSetup)
                            : ChatRuntimeCapabilityState.Unavailable,
                            vision ? null : FeatureExplanation("image understanding", visionDeclared, usable, waiting, needsSetup, _statusMessage)),
                        new("reasoning", "Careful reasoning", OptionalFeatureState(reasoningDeclared, usable, waiting, needsSetup),
                            FeatureExplanation("careful reasoning", reasoningDeclared, usable, waiting, needsSetup, _statusMessage)),
                        new("long-context", "Longer conversations", OptionalFeatureState(longContextDeclared, usable, waiting, needsSetup),
                            FeatureExplanation("longer conversations", longContextDeclared, usable, waiting, needsSetup, _statusMessage))
                    ],
                    Resources: resources,
                    SupportedImageMediaTypes: visionMediaTypes,
                    Activity: _currentDownloadProgress is { } download
                        ? new ChatRuntimeActivity("model-download", download.Description, Math.Clamp(download.Ratio, 0, 1))
                        : _downloadRecoveryMessage is { Length: > 0 } recovery
                            ? new ChatRuntimeActivity("model-download-paused", recovery)
                        : _accepting
                            ? new ChatRuntimeActivity("model-preparation", _statusMessage)
                            : null);
            }
        }
    }

    /// <summary>Refreshes the resource measurement used by Circle AI's device-fit decision.</summary>
    public ChatRuntimeCapabilityCatalog RefreshCapabilityCatalog()
    {
        try
        {
            var probe = SnapshotDevice(_options.ModelsDirectory);
            ModelSelection? pendingSelection = null;
            lock (_statusGate)
            {
                _deviceProbe = probe;
                _resourcesMeasuredAt = DateTimeOffset.UtcNow;

                // Resource availability can change after launch (for example, a WSL
                // workload can claim most of Desktop RAM). Reassess a waiting model
                // before continuing to advertise a stale download offer.
                if (!_isReady && !_accepting && _selected is { } selected
                    && IsWaitingForModelFit(_statusMessage))
                    pendingSelection = selected;
            }

            if (pendingSelection is not null)
            {
                using var selector = new CircleAiModelSelectorContext(_options.ModelsDirectory);
                selector.Assess(probe);
                var reassessed = selector.BestFit(probe, ChatCapability.Default | ChatCapability.Tools);
                if (reassessed.ModelId != pendingSelection.ModelId || reassessed.Quality != pendingSelection.Quality)
                {
                    lock (_statusGate)
                    {
                        if (!_isReady && !_accepting && _selected is not null)
                        {
                            _selected = reassessed;
                            _engineLabel = $"{reassessed.ModelId} (CircleAI)";
                        }
                    }

                    SetSelectedModelCapabilities(selector, probe, reassessed.ModelId);
                    if (reassessed.Quality == SelectionQuality.NothingFits)
                    {
                        SetStatus($"No Circle AI model fits this device's available resources. {reassessed.ModelId} is the smallest option; route this work to a more capable connected device.", ready: false);
                    }
                    else
                    {
                        var gigabytes = reassessed.EstimatedBytes / 1024d / 1024d / 1024d;
                        SetStatus($"{reassessed.ModelId} needs a {gigabytes:0.#} GB download before it can run.", ready: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CircleAI could not refresh its device resource measurement.");
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        return CapabilityCatalog;
    }

    private static bool IsWaitingForModelFit(string status)
        => status.StartsWith("No Circle AI model fits this device's available resources.", StringComparison.OrdinalIgnoreCase)
            || status.Contains("download before it can run.", StringComparison.OrdinalIgnoreCase);

    private DeviceProbe SnapshotDevice(string modelsDirectory)
        => _options.DeviceProbeFactory?.Invoke(modelsDirectory)
            ?? DeviceProbe.Snapshot(modelsDirectory);

    private void SetSelectedModelCapabilities(CircleAiModelSelectorContext selector, DeviceProbe probe, string modelId)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in new[]
        {
            ChatCapability.Default,
            ChatCapability.Tools,
            ChatCapability.Vision,
            ChatCapability.LongContext,
            ChatCapability.Reasoning
        })
        {
            if (HasDeclaredCapability(selector, probe, modelId, capability))
                declared.Add(capability.ToString());
        }

        lock (_statusGate)
            _selectedModelCapabilities = declared;
    }

    private static bool HasDeclaredCapability(
        CircleAiModelSelectorContext selector,
        DeviceProbe probe,
        string modelId,
        ChatCapability capability)
    {
        try
        {
            var selection = selector.BestFit(probe, ChatCapability.Default | ChatCapability.Tools | capability);
            return selection.Quality != SelectionQuality.NothingFits
                && string.Equals(selection.ModelId, modelId, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            // Optional capabilities may have no fit in the registry. That means the current
            // model does not advertise the feature; it must not take conversation startup
            // offline while we are only measuring what else this runtime can do.
            return false;
        }
    }

    private static ChatRuntimeCapabilityState OptionalFeatureState(
        bool declared,
        bool ready,
        bool preparing,
        bool actionRequired)
    {
        if (ready)
            return declared ? ChatRuntimeCapabilityState.Available : ChatRuntimeCapabilityState.Unavailable;
        if (!declared)
            return ChatRuntimeCapabilityState.Unavailable;
        if (preparing)
            return ChatRuntimeCapabilityState.Preparing;
        if (actionRequired)
            return ChatRuntimeCapabilityState.ActionRequired;
        return ChatRuntimeCapabilityState.Unavailable;
    }

    private static string? FeatureExplanation(
        string name,
        bool declared,
        bool ready,
        bool preparing,
        bool actionRequired,
        string status)
    {
        if (ready)
            return declared ? null : $"The current Circle AI setup does not include {name}.";
        if (declared && (preparing || actionRequired))
            return status;
        return $"Circle AI does not report {name} as available on this device.";
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = loading.Token;
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        lock (_statusGate)
        {
            if (_disposed)
                return;
            _loadStarted = true;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_options.NativeLibDir))
                NativeLibraryResolver.OverrideDirectory = _options.NativeLibDir;
            NativeLibraryResolver.EnsureRegistered();

            SetStatus("Picking model for this device…", ready: false);

            // Catalog-driven selection. The old static ModelSelector was removed upstream as
            // an architecture violation: it hardcoded the tiers rather than reading them from
            // the registry, so a new model could not be offered without a code change.
            var modelsDirectory = _options.ModelsDirectory;
            Directory.CreateDirectory(modelsDirectory);

            // DeviceProbe's generic desktop estimate is based on physical RAM.
            // That misses memory consumed by other processes (notably WSL), so a
            // large MoE can be selected even when Windows has almost no RAM left.
            // Keep this callback installed in the isolated Windows host so both
            // initial selection and later readiness refreshes use live available RAM.
            if (_options.DeviceProbeFactory is null && OperatingSystem.IsWindows())
                DeviceProbe.PlatformMemoryProbe = WindowsAvailableMemoryProbe.Read;

            var probe = SnapshotDevice(modelsDirectory);
            using var selector = new CircleAiModelSelectorContext(modelsDirectory);
            selector.Assess(probe);
            lock (_statusGate)
            {
                _deviceProbe = probe;
                _resourcesMeasuredAt = DateTimeOffset.UtcNow;
            }

            // Tools, because Concierge's whole tool loop depends on the model emitting call
            // blocks. Asking for it here means an unsuitable model is refused at selection
            // rather than discovered when the first tool call comes back as prose.
            var tier = selector.BestFit(probe, ChatCapability.Default | ChatCapability.Tools);
            var stagingCandidates = _options.StageEligibleModelsSmallestFirst
                ? selector.EligibleCandidatesSmallestFirst(probe, ChatCapability.Default | ChatCapability.Tools)
                : new[] { tier };
            var selectedIndex = stagingCandidates
                .Select((candidate, index) => (candidate, index))
                .Where(item => string.Equals(item.candidate.ModelId, tier.ModelId, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            lock (_statusGate)
            {
                _stagingPlan = tier.Quality == SelectionQuality.NothingFits
                    ? []
                    : selectedIndex >= 0
                        ? stagingCandidates.Take(selectedIndex + 1).ToArray()
                        : [tier];
            }
            SetSelectedModelCapabilities(selector, probe, tier.ModelId);
            _engineLabel = $"{tier.ModelId} (CircleAI)";

            // The selector always answers RequiresDownload: true — its own comment says
            // "selector cannot tell — caller checks the cache". This is that caller. Without
            // this check the app asks for the same 21 GB on every launch, including the one
            // right after it finished downloading it, and never comes up ready at all.
            var alreadyHere = IsAlreadyDownloaded(tier.ModelId, modelsDirectory);

            if (tier.Quality == SelectionQuality.NothingFits)
            {
                lock (_statusGate)
                    _selected = tier;
                SetStatus($"No Circle AI model fits this device's available resources. {tier.ModelId} is the smallest option; route this work to a more capable connected device.", ready: false);
                _generatorReady.TrySetResult(null);
                return;
            }

            // A model that is not on disk is not fetched here. RequiresDownload plus the
            // measured byte count is what the person needs in order to decide, and starting a
            // multi-gigabyte transfer on their connection without asking is not ours to do.
            if (tier.RequiresDownload && !alreadyHere)
            {
                // Before asking anybody to wait an hour: is there a model on this device that
                // works right now? Best fit is a judgement about quality, and quality is worth
                // nothing to somebody who cannot use the app yet. Take what is here, come up
                // ready, and go on offering the better one.
                var usable = selector.AllCandidates(probe)
                .FirstOrDefault(candidate =>
                    candidate.Quality != SelectionQuality.NothingFits &&
                    IsAlreadyDownloaded(candidate.ModelId, modelsDirectory));

                lock (_statusGate)
                {
                    _selected = tier;
                }

                if (usable is not null)
                {
                    SetSelectedModelCapabilities(selector, probe, usable.ModelId);
                    _engineLabel = $"{usable.ModelId} (CircleAI)";
                    SetStatus($"Loading {usable.ModelId} into memory (one-time)…", ready: false);
                    var usablePath = await FetchAsync(usable.ModelId, null, cancellationToken).ConfigureAwait(false);
                    await ActivateModelAsync(usable.ModelId, usablePath, cancellationToken).ConfigureAwait(false);
                    SetStatus($"Ready · {usable.ModelId}", ready: true);
                    return;
                }

                if (_options.AllowAutomaticDownload)
                {
                    SetStatus($"Circle AI selected {tier.ModelId} for this device and is preparing it automatically ({tier.EstimatedBytes / 1024d / 1024d / 1024d:0.#} GB).", ready: false);
                    _generatorReady.TrySetResult(null);
                    return;
                }

                var gigabytes = tier.EstimatedBytes / 1024d / 1024d / 1024d;
                SetStatus($"{tier.ModelId} needs a {gigabytes:0.#} GB download before it can run.", ready: false);
                _generatorReady.TrySetResult(null);
                return;
            }

            SetStatus($"Resolving model path for {tier.ModelId}…", ready: false);

            var modelPath = await FetchAsync(tier.ModelId, null, cancellationToken).ConfigureAwait(false);

            SetStatus($"Loading {tier.ModelId} into memory (one-time)…", ready: false);
            await ActivateModelAsync(tier.ModelId, modelPath, cancellationToken).ConfigureAwait(false);

            SetStatus($"Ready · {tier.ModelId}", ready: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Engine load cancelled.", ready: false);
            _generatorReady.TrySetResult(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CircleAI chat runtime failed to load.");
            SetStatus($"Engine offline: {ex.Message}", ready: false);
            _generatorReady.TrySetResult(null);
        }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    /// <summary>
    /// Writes the exact model plan after the person accepts it and before an Android job is
    /// scheduled. The OS job itself then becomes the durable owner of the transfer.
    /// </summary>
    public async Task<bool> PrepareDownloadApprovalAsync(CancellationToken cancellationToken = default)
    {
        RefreshCapabilityCatalog();

        ModelSelection? selection;
        IReadOnlyList<ModelSelection> stagingPlan;
        lock (_statusGate)
        {
            if (_disposed || _accepting)
                return false;
            selection = _selected;
            stagingPlan = _stagingPlan;
        }

        if (selection is null || selection.Quality == SelectionQuality.NothingFits)
            return false;

        var candidates = _options.StageEligibleModelsSmallestFirst
            ? stagingPlan
                .Where(candidate => candidate.EstimatedBytes <= selection.EstimatedBytes)
                .OrderBy(candidate => candidate.EstimatedBytes)
                .ThenBy(candidate => candidate.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [selection];
        if (!candidates.Any(candidate => string.Equals(candidate.ModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase)))
            candidates = candidates.Append(selection).OrderBy(candidate => candidate.EstimatedBytes).ToArray();

        await _downloadApprovalStore.WriteAsync(new ModelDownloadApproval(
            SchemaVersion: 1,
            SelectedModelId: selection.ModelId,
            StagedModelIds: candidates.Select(candidate => candidate.ModelId).ToArray(),
            AcceptedAtUtc: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        lock (_statusGate)
        {
            _currentDownloadProgress = new ModelDownloadProgress(
                0,
                "Waiting for Circle AI's background download worker to start");
            _statusMessage = "Circle AI download accepted; starting background preparation…";
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> AcceptDownloadAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The resources that justified this offer may have changed since the last
        // capability refresh. Re-run Circle AI's selector before accepting it.
        RefreshCapabilityCatalog();

        ModelSelection selection;
        IReadOnlyList<ModelSelection> stagingPlan;
        lock (_statusGate)
        {
            if (_disposed)
            {
                return false;
            }

            // Without this, two clicks build two generators, each holding the weights. The
            // first would be replaced and never disposed — gigabytes, on a device that may
            // only have two of them.
            if (_accepting)
            {
                return false;
            }

            if (_selected is not { } pending)
            {
                // Nothing pending can mean two very different things: nothing has been picked
                // yet, or a model is loaded and there is nothing left to fetch. Saying "no
                // model has been selected" in the second case, and dropping IsReady to say it,
                // takes a working engine offline over a stray call.
                if (!_isReady)
                {
                    _statusMessage = "No model has been selected yet.";
                }

                return false;
            }

            if (pending.Quality == SelectionQuality.NothingFits)
                return false;

            selection = pending;
            stagingPlan = _stagingPlan;
            _accepting = true;
            _downloadRecoveryMessage = null;
        }

        // Linked so that closing the app stops the transfer, not only the person clicking stop.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lock (_statusGate)
            _downloadCancellation = stopping;

        var preserveApprovalOnExit = false;
        try
        {
            var candidates = _options.StageEligibleModelsSmallestFirst
                ? stagingPlan
                    .Where(candidate => candidate.EstimatedBytes <= selection.EstimatedBytes)
                    .OrderBy(candidate => candidate.EstimatedBytes)
                    .ThenBy(candidate => candidate.ModelId, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [selection];
            if (!candidates.Any(candidate => string.Equals(candidate.ModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase)))
                candidates = candidates.Append(selection).OrderBy(candidate => candidate.EstimatedBytes).ToArray();

            ModelDownloadApproval? resumeApproval;
            lock (_statusGate)
            {
                resumeApproval = _resumeApproval;
                _resumeApproval = null;
                _explicitlyStopped = false;
            }

            if (resumeApproval is not null)
            {
                var currentIds = candidates.Select(candidate => candidate.ModelId).ToArray();
                if (!string.Equals(resumeApproval.SelectedModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase)
                    || !resumeApproval.StagedModelIds.SequenceEqual(currentIds, StringComparer.OrdinalIgnoreCase))
                {
                    await _downloadApprovalStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                    SetStatus("The previous Circle AI download offer has changed. Review the current offer to continue.", ready: IsReady);
                    return false;
                }
            }
            else
            {
                // Record the person's consent before any bundle request begins. If the process
                // exits mid-transfer, the next runtime can resume this exact model plan only.
                await _downloadApprovalStore.WriteAsync(new ModelDownloadApproval(
                    SchemaVersion: 1,
                    SelectedModelId: selection.ModelId,
                    StagedModelIds: candidates.Select(candidate => candidate.ModelId).ToArray(),
                    AcceptedAtUtc: DateTimeOffset.UtcNow), stopping.Token).ConfigureAwait(false);
                stopping.Token.ThrowIfCancellationRequested();
            }

            ReportDownloadProgress(
                new ModelDownloadProgress(0, $"Preparing the first of {candidates.Length} Circle AI model downloads"),
                progress);

            var remaining = candidates
                .Where(candidate => !IsAlreadyDownloaded(candidate.ModelId, _options.ModelsDirectory))
                .ToList();
            if (!remaining.Any(candidate => string.Equals(candidate.ModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase)))
                remaining.Add(selection);

            var totalBytes = remaining.Sum(candidate => Math.Max(candidate.EstimatedBytes, 1));
            var bytesComplete = 0L;
            var downloadedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < remaining.Count; index++)
            {
                stopping.Token.ThrowIfCancellationRequested();
                var candidate = remaining[index];
                var stageNumber = index + 1;
                SetStatus(
                    $"Preparing Circle AI · downloading model {stageNumber} of {remaining.Count}",
                    ready: IsReady);

                var stageProgress = new InlineProgress<ModelDownloadProgress>(report =>
                {
                    var stageBytes = (long)(Math.Max(candidate.EstimatedBytes, 1) * Math.Clamp(report.Ratio, 0, 1));
                    var overallRatio = totalBytes == 0
                        ? report.Ratio
                        : Math.Clamp((bytesComplete + stageBytes) / (double)totalBytes, 0, 1);
                    ReportDownloadProgress(new ModelDownloadProgress(
                        overallRatio,
                        $"Model {stageNumber} of {remaining.Count} · {report.Description}"), progress);
                });

                var modelPath = await FetchAsync(candidate.ModelId, stageProgress, stopping.Token).ConfigureAwait(false);
                stopping.Token.ThrowIfCancellationRequested();
                downloadedPaths[candidate.ModelId] = modelPath;
                bytesComplete += Math.Max(candidate.EstimatedBytes, 1);

                // The smallest compatible model makes the app useful as soon as its bundle
                // lands. The larger selected tier is fetched afterwards while this model
                // remains available for conversations and tool work.
                if (!IsReady)
                {
                    stopping.Token.ThrowIfCancellationRequested();
                    var probe = SnapshotDevice(_options.ModelsDirectory);
                    using var selector = new CircleAiModelSelectorContext(_options.ModelsDirectory);
                    selector.Assess(probe);
                    var stillEligible = selector.EligibleCandidatesSmallestFirst(
                        probe, ChatCapability.Default | ChatCapability.Tools)
                        .Any(item => string.Equals(item.ModelId, candidate.ModelId, StringComparison.OrdinalIgnoreCase));
                    if (!stillEligible)
                    {
                        _ = _generatorReady.TrySetResult(null);
                        SetStatus("Device resources changed before Circle AI could open the starting model. The download is saved; refresh readiness to retry.", ready: false);
                        return false;
                    }

                    SetSelectedModelCapabilities(selector, probe, candidate.ModelId);
                    _engineLabel = $"{candidate.ModelId} (CircleAI)";
                    SetStatus("Opening the first Circle AI model…", ready: false);
                    if (!await ActivateModelAsync(candidate.ModelId, modelPath, stopping.Token).ConfigureAwait(false))
                        return false;
                    SetStatus("Circle AI is ready with a starting model. The selected model is preparing in the background.", ready: true);
                }

                ReportDownloadProgress(new ModelDownloadProgress(
                    totalBytes == 0 ? 1 : Math.Clamp(bytesComplete / (double)totalBytes, 0, 1),
                    $"Downloaded Circle AI model {stageNumber} of {remaining.Count}"), progress);
            }

            var selectedPath = downloadedPaths.TryGetValue(selection.ModelId, out var downloadedPath)
                ? downloadedPath
                : await FetchAsync(selection.ModelId, null, stopping.Token).ConfigureAwait(false);

            if (!string.Equals(_activeModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                var latestProbe = SnapshotDevice(_options.ModelsDirectory);
                using var selector = new CircleAiModelSelectorContext(_options.ModelsDirectory);
                selector.Assess(latestProbe);
                var latestSelection = selector.BestFit(latestProbe, ChatCapability.Default | ChatCapability.Tools);
                if (latestSelection.Quality == SelectionQuality.NothingFits
                    || !string.Equals(latestSelection.ModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase))
                {
                    lock (_statusGate)
                    {
                        _deviceProbe = latestProbe;
                        _resourcesMeasuredAt = DateTimeOffset.UtcNow;
                        _selected = latestSelection;
                        _engineLabel = $"{latestSelection.ModelId} (CircleAI)";
                    }
                    SetSelectedModelCapabilities(selector, latestProbe, latestSelection.ModelId);
                    SetStatus(IsReady
                        ? "Circle AI is ready with a compatible starting model. Device capacity changed; the best-fit model is saved for a later retry."
                        : $"Device resources changed. Circle AI selected a different model: {latestSelection.ModelId}.", ready: IsReady);
                    return IsReady;
                }

                SetSelectedModelCapabilities(selector, latestProbe, selection.ModelId);
                SetStatus("Switching to the best-fit Circle AI model…", ready: IsReady);
                if (!await ActivateModelAsync(selection.ModelId, selectedPath, stopping.Token).ConfigureAwait(false))
                    return IsReady;
            }

            lock (_statusGate)
            {
                _selected = null;
            }

            SetStatus($"Ready · {selection.ModelId}", ready: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            lock (_statusGate)
                preserveApprovalOnExit = !_explicitlyStopped;
            SetStatus(IsReady
                ? $"Preparation paused. Continuing with {EngineLabel}."
                : "Model download stopped. Circle AI can retry the download.", ready: IsReady);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CircleAI model download failed.");
            SetStatus(IsReady
                ? $"Could not prepare {selection.ModelId}; continuing with {EngineLabel}. {ex.Message}"
                : $"Circle AI could not prepare {selection.ModelId}: {ex.Message}. You can retry from this download offer.", ready: IsReady);
            return false;
        }
        finally
        {
            if (!preserveApprovalOnExit)
            {
                try
                {
                    await _downloadApprovalStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not clear the completed Circle AI download approval.");
                }
            }

            lock (_statusGate)
            {
                _accepting = false;
                _currentDownloadProgress = null;
                _downloadCancellation = null;
            }
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task CancelCurrentDownloadAsync()
    {
        CancellationTokenSource? stopping;
        lock (_statusGate)
        {
            _explicitlyStopped = true;
            stopping = _downloadCancellation;
            if (stopping is null)
                _currentDownloadProgress = null;
        }

        try
        {
            stopping?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The transfer completed between reading the source and cancelling it.
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        return _downloadApprovalStore.ClearAsync(CancellationToken.None);
    }

    /// <summary>
    /// Stops the transfer because its background owner is shutting down or pausing. Unlike the
    /// person's Stop action, this preserves the accepted model plan for safe continuation later.
    /// </summary>
    public Task PauseCurrentDownloadAsync()
    {
        CancellationTokenSource? stopping;
        lock (_statusGate)
            stopping = _downloadCancellation;

        try
        {
            stopping?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It completed while the pause was being delivered.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes a previously accepted transfer after the app recreated its runtime. Resume is
    /// allowed only when Circle AI still selects the same model and the staged plan is unchanged.
    /// If resources changed, keep the accepted record and partial files, then expose a review
    /// message; silently deleting the person's accepted work makes restart recovery impossible.
    /// </summary>
    public async Task<bool> ResumeAcceptedDownloadAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ModelDownloadApproval? approval;
        try
        {
            approval = await _downloadApprovalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Could not read the prior Circle AI download approval; keeping setup manual.");
            await _downloadApprovalStore.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (approval is null)
            return false;

        RefreshCapabilityCatalog();
        ModelSelection? selection;
        IReadOnlyList<ModelSelection> stagingPlan;
        lock (_statusGate)
        {
            selection = _selected;
            stagingPlan = _stagingPlan;
        }

        if (selection is null || selection.Quality == SelectionQuality.NothingFits
            || !string.Equals(approval.SelectedModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            MarkDownloadPlanChanged();
            return false;
        }

        var candidates = _options.StageEligibleModelsSmallestFirst
            ? stagingPlan
                .Where(candidate => candidate.EstimatedBytes <= selection.EstimatedBytes)
                .OrderBy(candidate => candidate.EstimatedBytes)
                .ThenBy(candidate => candidate.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [selection];
        if (!candidates.Any(candidate => string.Equals(candidate.ModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase)))
            candidates = candidates.Append(selection).OrderBy(candidate => candidate.EstimatedBytes).ToArray();

        if (!approval.StagedModelIds.SequenceEqual(candidates.Select(candidate => candidate.ModelId), StringComparer.OrdinalIgnoreCase))
        {
            MarkDownloadPlanChanged();
            return false;
        }

        lock (_statusGate)
            _resumeApproval = approval;
        return await AcceptDownloadAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    private void MarkDownloadPlanChanged()
    {
        var message = IsReady
            ? "The previously accepted Circle AI download is paused because device resources changed. Partial model files are kept. Circle AI can keep answering; review the current offer when available."
            : "The previously accepted Circle AI download is paused because device resources changed. Partial model files are kept. Review the current offer when available.";
        lock (_statusGate)
            _downloadRecoveryMessage = message;
        SetStatus(message, ready: IsReady);
    }

    /// <summary>
    /// Fetches a model bundle. Goes through <see cref="CircleAiChatOptions.ModelFetcher"/> when
    /// one is supplied, so a host can point at its own mirror and a test can avoid the network.
    /// </summary>
    private async Task<string> FetchAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_options.ModelFetcher is { } fetcher)
        {
            return await fetcher(modelId, progress, cancellationToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(_options.ModelsDirectory);

        // BundleModelLoader, not LocalModelManager. Every entry in the registry is
        // bundle-shaped, and the legacy manager throws on all of them — which is why the
        // engine never came up on any machine. It also returns the weight blob rather than
        // config.json, which is what MNN's Llm::create() actually loads, so even a downloaded
        // bundle would have failed at the next step.
        using var loader = new BundleModelLoader(_options.ModelsDirectory);

        // The rich overload, not the IProgress<float> one: that one drops the byte counts and
        // the ETA one call before the screen, and takes no cancellation token — so a stop
        // button wired to it would be a lie.
        // Progress<T> posts once per downloader chunk. A large bundle can produce hundreds
        // of callbacks per second across parallel ranges; on Android those queued callbacks
        // competed with the UI thread and drove Mono into repeated full GCs. Relay inline so
        // the platform worker can apply its own bounded notification cadence.
        var relayed = progress is null
            ? null
            : new InlineProgress<DownloadProgress>(report =>
                progress.Report(new ModelDownloadProgress(report.Ratio, report.Describe())));

        return await loader.DownloadModelAsync(modelId, relayed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Is this model on the device already? Cheap on purpose — presence, not integrity.
    /// </summary>
    /// <remarks>
    /// CircleAI draws that distinction and documents why: verifying the anchor file's SHA-256
    /// means hashing hundreds of megabytes, which is fine before loading a model and ruinous on
    /// a launch screen. The load path hashes; this only asks whether it is worth trying.
    /// </remarks>
    private bool IsAlreadyDownloaded(string modelId, string modelsDirectory)
    {
        try
        {
            if (_options.ModelPresence is { } present)
            {
                return present(modelId);
            }

            using var loader = new BundleModelLoader(modelsDirectory);
            return loader.ModelPresent(modelId);
        }
        catch (Exception exception)
        {
            // Never a reason to fail the launch. Not knowing means asking, which is the safe
            // side of this particular question.
            _logger.LogWarning(exception, "Could not tell whether {ModelId} is on the device.", modelId);
            return false;
        }
    }

    /// <summary>
    /// The generator for this model — and, for a vision family, the one that can see.
    ///
    /// `KimiVlGenerator` takes the image bytes on a turn and feeds them through the model's
    /// own vision encoder; `QwenTextGenerator` has nowhere to put them. Which one is built is
    /// decided by what the model is, because loading a text model through the vision
    /// generator asks a text model for a picture it cannot take.
    ///
    /// Whether it can actually see is then read back from the model rather than from its
    /// name: `IsVisionCapable` is the native runtime's own answer. A file named like a vision
    /// model that is not one says so, instead of being advertised on the strength of its
    /// filename.
    /// </summary>
    private IChatGenerator CreateGenerator(string modelPath)
        => _options.GeneratorFactory is { } factory
            ? factory(modelPath)
            : LooksLikeItCanSee(modelPath)
                ? new KimiVlGenerator(modelPath, _options.ContextSize)
                : new QwenTextGenerator(modelPath, _options.ContextSize);

    /// <summary>
    /// Whether this model is from a family that sees, by the name it was published under.
    ///
    /// A guess, and only used to decide which generator to build — never to claim the
    /// capability. The claim comes from the model itself.
    /// </summary>
    public static bool LooksLikeItCanSee(string modelPath)
    {
        var name = modelPath.Replace(Path.DirectorySeparatorChar, '/');

        return name.Contains("-vl", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || name.Contains("kimi-vl", StringComparison.OrdinalIgnoreCase)
            || name.Contains("llava", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What this model can be shown, which is nothing at all unless a vision model is loaded.
    ///
    /// Empty is the ordinary answer and is meant to be read as "cannot see" — the composer
    /// checks the list rather than only the interface, so a text model does not silently
    /// accept a picture it will ignore.
    /// </summary>
    public IReadOnlyCollection<string> SupportedImageMediaTypes
    {
        get
        {
            IChatGenerator? generator;
            lock (_statusGate)
            {
                generator = _generator;
            }

            return generator is KimiVlGenerator seeing && seeing.IsVisionCapable
                ? ["image/jpeg", "image/png", "image/webp"]
                : [];
        }
    }

    private void ReportDownloadProgress(
        ModelDownloadProgress report,
        IProgress<ModelDownloadProgress>? progress = null)
    {
        bool notifyStatus;
        lock (_statusGate)
        {
            _currentDownloadProgress = report;
            var now = Stopwatch.GetTimestamp();
            notifyStatus = report.Ratio >= 1
                || _lastProgressStatusTimestamp == 0
                || Stopwatch.GetElapsedTime(_lastProgressStatusTimestamp, now) >= TimeSpan.FromMilliseconds(500);
            if (notifyStatus)
                _lastProgressStatusTimestamp = now;
        }
        progress?.Report(report);
        if (notifyStatus)
            StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens a model with exclusive access to the native handle. A larger replacement can
    /// need more memory than the old one plus the new one, so the old generator is freed
    /// before the replacement is created. A failed upgrade reloads the prior model.
    /// </summary>
    private async Task<bool> ActivateModelAsync(
        string modelId,
        string modelPath,
        CancellationToken cancellationToken)
    {
        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IChatGenerator? replaced = null;
        string? replacedId = null;
        string? replacedPath = null;
        try
        {
            lock (_statusGate)
            {
                if (_disposed)
                    return false;

                if (_generator is not null
                    && string.Equals(_activeModelId, modelId, StringComparison.OrdinalIgnoreCase))
                    return true;

                replaced = _generator;
                replacedId = _activeModelId;
                replacedPath = _activeModelPath;
                _generator = null;
                _activeModelId = null;
                _activeModelPath = null;
                _isReady = false;
                _generatorReady = new TaskCompletionSource<IChatGenerator?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            replaced?.Dispose();
            replaced = null;

            try
            {
                var generator = CreateGenerator(modelPath);
                if (!Publish(generator, modelId, modelPath))
                {
                    generator.Dispose();
                    return false;
                }

                return true;
            }
            catch when (!string.IsNullOrWhiteSpace(replacedId) && !string.IsNullOrWhiteSpace(replacedPath))
            {
                // A higher tier may fail to open under the memory actually available after
                // download. Restore the last usable tier instead of leaving Chat offline.
                var fallback = CreateGenerator(replacedPath!);
                if (Publish(fallback, replacedId!, replacedPath!))
                {
                    _engineLabel = $"{replacedId} (CircleAI)";
                    SetStatus("The larger Circle AI model could not open. Continuing with the model already prepared on this device.", ready: true);
                    return false;
                }

                fallback.Dispose();
                return false;
            }
        }
        catch
        {
            lock (_statusGate)
            {
                if (!_generatorReady.Task.IsCompleted)
                    _generatorReady.TrySetResult(null);
            }
            throw;
        }
        finally
        {
            _generationGate.Release();
        }
    }

    /// <summary>
    /// Acquires the generation gate and returns the current model. A caller can observe an
    /// old readiness task just before a tier switch, so it rechecks identity after the gate.
    /// The caller releases the gate after using the returned generator.
    /// </summary>
    private async Task<IChatGenerator?> AcquireGeneratorAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<IChatGenerator?> readiness;
            lock (_statusGate)
            {
                if (_disposed)
                    return null;
                if (_generator is null && !_loadStarted)
                    return null;
                readiness = _generatorReady.Task;
            }

            var candidate = await readiness.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (candidate is null)
                return null;

            await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_statusGate)
            {
                if (_disposed)
                {
                    _generationGate.Release();
                    return null;
                }

                if (ReferenceEquals(candidate, _generator))
                    return candidate;
            }

            _generationGate.Release();
        }
    }

    /// <summary>
    /// Makes <paramref name="generator"/> the one this runtime owns, disposing whatever it
    /// replaces. Returns false when the runtime has been disposed, in which case the caller
    /// owns the generator it just built.
    /// </summary>
    private bool Publish(IChatGenerator generator, string modelId, string modelPath)
    {
        IChatGenerator? replaced;
        lock (_statusGate)
        {
            if (_disposed)
            {
                return false;
            }

            replaced = _generator;
            _generator = generator;
            _activeModelId = modelId;
            _activeModelPath = modelPath;
            _engineLabel = $"{modelId} (CircleAI)";

            // Whoever was waiting while there was no model already has their answer. A fresh
            // source is what the next caller waits on.
            var ready = new TaskCompletionSource<IChatGenerator?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ready.SetResult(generator);
            _generatorReady = ready;
        }

        replaced?.Dispose();
        return true;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Checked before the gate: WaitAsync on a disposed SemaphoreSlim throws, and a shut-down
        // engine should answer like an engine that has no model, not blow up the page.
        lock (_statusGate)
        {
            if (_disposed)
            {
                yield return "[The engine has shut down.]";
                yield break;
            }
        }

        var generator = await AcquireGeneratorAsync(cancellationToken).ConfigureAwait(false);
        if (generator is null)
        {
            yield return $"[{StatusMessage}]";
            yield break;
        }

        // The image channel on a turn, carried through to the model's own. It has existed on
        // ChatTurn since vision input was added and has only ever reached the cloud runtimes;
        // this is the local end of it. A text generator ignores ImageBytes, so the worst a
        // picture on a text model costs is nothing — and the composer will not send one
        // anyway, because SupportedImageMediaTypes is empty until a vision model is loaded.
        //
        // One picture rather than several: MNN takes a single image per turn, and quietly
        // dropping the rest would be worse than saying so, which SupportedImageMediaTypes
        // being read by the composer already handles.
        var translated = messages
            .Select(turn => new ChatMessage(turn.Role, turn.Content)
            {
                ImageBytes = turn.Images is { Count: > 0 } pictures ? pictures[0].Bytes : null,
            })
            .ToList();

        // Per-call generation knobs:
        //   * Budget = Normal — the runtime caps tokens at ~512, uses TQ4 KV
        //     compression, and automatically downgrades to Low when battery
        //     drops below 15%. Matches Concierge's "snappy chat reply" shape;
        //     callers that need long planning replies can override per-message
        //     once that surface exists.
        //   * UsePrefixCache = false — and this is why Concierge can hold a
        //     conversation at all.
        //
        //     The prefix cache is keyed on (modelId, systemPrompt), so it only
        //     engages once a system turn is present. That path faults in the
        //     native generator: the process prints "Fatal error." and dies,
        //     taking the reply with it. Because the app always sends a system
        //     prompt, it died on every single turn, and because a bare
        //     `Concierge.Model.Host` run by hand sends none, it looked healthy
        //     every time anyone checked.
        //
        //     Measured rather than reasoned about, three payloads through the
        //     host: a 3,075-char *user* prompt completes (cache=off); a
        //     16-char *system* turn faults at 121 chars fed (cache=on); the
        //     same 6,882-char payload faults as a system turn and completes as
        //     a user turn. Size is irrelevant — the system role is the trigger.
        //
        //     This was recorded as "not doing, it is the CircleAI package" and
        //     that was wrong twice over: the switch is ours, and turning it off
        //     costs sub-200ms first-token latency (RT-06) rather than a
        //     conversation. Turn it back on when the native cache path is
        //     fixed, and re-run the three payloads above before believing it.
        var options = new GenerationOptions
        {
            Budget = PowerBudget.Normal,
            UsePrefixCache = _options.UsePrefixCache,
            MaxTokens = (int)_options.MaxOutputTokens,
        };

        try
        {
            await foreach (var chunk in generator
                .StreamAsync(translated, options, ct: cancellationToken)
                .ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            _generationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SaveSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var generator = await AcquireGeneratorAsync(cancellationToken).ConfigureAwait(false);
        if (generator is null)
        {
            // Engine never loaded — nothing to snapshot. Not an error from the
            // host's perspective; OnSleep happens whether or not the user has
            // typed anything.
            return false;
        }

        try
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return await generator.SaveSessionAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CircleAI session snapshot to {Path} failed; conversation will start cold on resume.",
                    path);
                return false;
            }
        }
        finally
        {
            _generationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> LoadSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var generator = await AcquireGeneratorAsync(cancellationToken).ConfigureAwait(false);
        if (generator is null)
        {
            return false;
        }

        try
        {
            try
            {
                return await generator.LoadSessionAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CircleAI session restore from {Path} failed; falling back to cold session.",
                    path);
                return false;
            }
        }
        finally
        {
            _generationGate.Release();
        }
    }

    /// <summary>
    /// Stops anything in flight and frees the model.
    /// </summary>
    /// <remarks>
    /// Order matters. Cancelling first stops a download that would otherwise keep running
    /// against somebody's data after the app is gone. Waiting on the generation gate keeps the
    /// native handle alive until the decode using it has finished — freeing it underneath a
    /// running generation is a crash, not an exception.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        lock (_statusGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generatorReady.TrySetResult(null);
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        var loadHeld = false;
        try
        {
            loadHeld = await _loadGate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already gone; no load can still be using the generator.
        }

        var held = false;
        try
        {
            held = await _generationGate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already gone; nothing is decoding.
        }

        IChatGenerator? generator;
        lock (_statusGate)
        {
            generator = _generator;
            _generator = null;
        }

        generator?.Dispose();

        if (held)
        {
            _generationGate.Release();
        }

        _generationGate.Dispose();
        if (loadHeld)
        {
            _loadGate.Release();
            _loadGate.Dispose();
        }
        _lifetime.Dispose();
    }

    private void SetStatus(string message, bool ready)
    {
        lock (_statusGate)
        {
            _statusMessage = message;
            _isReady = ready;
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsPreparingStatus(string status, bool accepting)
        => accepting
            || status.Contains("queued", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Picking model", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Resolving model", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Loading ", StringComparison.OrdinalIgnoreCase)
            || status.Contains("preparing it automatically", StringComparison.OrdinalIgnoreCase);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>
/// Construction-time knobs for <see cref="CircleAiChatRuntime"/>. Defaults assume the
/// model store lives under <c>%LocalAppData%/Concierge/models</c> and that downloads,
/// when needed, come from CircleAI's default ModelScope source.
/// </summary>
public sealed class CircleAiChatOptions
{
    /// <summary>Optional native library directory supplied by a mobile application host.</summary>
    public string? NativeLibDir { get; init; }

    /// <summary>
    /// Supplies a live resource snapshot for this host. Production uses Circle AI's
    /// platform probe; deterministic test hosts may supply a fixed snapshot so selection
    /// tests do not change when the test runner's memory limit or machine load changes.
    /// </summary>
    public Func<string, DeviceProbe>? DeviceProbeFactory { get; init; }

    /// <summary>
    /// How a model bundle is fetched. Null means CircleAI's <c>BundleModelLoader</c> against
    /// the default source — what ships. A host with its own mirror can substitute one, and a
    /// test can supply one that never touches the network, which is the only way to exercise
    /// this path without pulling twenty-one gigabytes.
    /// </summary>
    public Func<string, IProgress<ModelDownloadProgress>?, CancellationToken, Task<string>>? ModelFetcher { get; init; }

    /// <summary>
    /// Whether a model is already on the device. Null means CircleAI's
    /// <c>BundleModelLoader.ModelPresent</c>, which checks presence without hashing.
    /// </summary>
    public Func<string, bool>? ModelPresence { get; init; }

    /// <summary>
    /// How a generator is built from a model path. Null means <c>QwenTextGenerator</c>, which
    /// opens the native model. Substitutable for the same reason as
    /// <see cref="ModelFetcher"/> — a real one needs real weights on disk.
    /// </summary>
    public Func<string, IChatGenerator>? GeneratorFactory { get; init; }

    public string ModelsDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Concierge",
        "models");

    /// <summary>
    /// Repository URL handed to <see cref="CircleAI.Core.LocalModelManager"/>. Non-null
    /// turns on the ModelScope downloader; null means the host must have pre-staged the
    /// model files on disk under <see cref="ModelsDirectory"/>.
    /// </summary>
    public Uri? RepositoryUrl { get; init; } = new("https://modelscope.cn/");

    public uint ContextSize { get; init; } = 4096;

    /// <summary>
    /// Cap on output tokens per generation. Surfaces
    /// <see cref="CircleAI.Inference.GenerationOptions.MaxTokens"/>; the active
    /// <see cref="CircleAI.Inference.PowerBudget"/> may tighten this further on
    /// low-battery devices but never widens it past this value.
    /// </summary>
    public uint MaxOutputTokens { get; init; } = 512;

    /// <summary>
    /// Whether the native prefill KV cache may be used.
    /// </summary>
    /// <remarks>
    /// False on purpose, and the product does not work with it on. The cache is
    /// keyed on (modelId, systemPrompt), so it engages only once a system turn
    /// is present — and that path faults inside the native generator, killing
    /// the host process mid-reply. Since every real turn carries a system
    /// prompt, that was every turn. It is an option rather than a literal so a
    /// test can hold the default and so it can be switched back on deliberately,
    /// with evidence, once the native path is fixed.
    /// </remarks>
    public bool UsePrefixCache { get; init; }

    /// <summary>
    /// Whether the engine may fetch a model it does not have, without being asked.
    /// </summary>
    /// <remarks>
    /// False on purpose. The registry's desktop-tier bundle is 22.8 GB — measured, not
    /// estimated — and on a P30 Lite over wifi that is the better part of an hour. On mobile
    /// data it is somebody's month. A download that size is a decision the person paying for
    /// the connection makes, so <see cref="CircleAiChatRuntime.LoadAsync"/> reports what is
    /// needed and stops, and a host calls
    /// <see cref="CircleAiChatRuntime.EnsureModelAsync"/> once somebody has said yes.
    /// </remarks>
    public bool AllowAutomaticDownload { get; init; }

    /// <summary>
    /// Whether an accepted setup download prepares every currently eligible chat/tool tier
    /// from smallest to the device-selected best fit. No extra models are fetched unless the
    /// person starts setup or automatic preparation is configured.
    /// </summary>
    public bool StageEligibleModelsSmallestFirst { get; init; }

    /// <summary>
    /// Whether the app's in-process startup loader resumes accepted downloads. Android
    /// JobScheduler owns accepted transfers there, so the loader must not race the job.
    /// </summary>
    public bool ResumeAcceptedDownloadOnStartup { get; init; } = true;

    /// <summary>
    /// Per-(installation, conversation) path the MAUI host uses to snapshot the
    /// active session on <c>OnSleep</c> and restore on <c>OnResume</c>. When
    /// null/empty, lifecycle persistence is disabled and the conversation lives
    /// entirely in the SQLite store. Defaults to a per-user file under
    /// <c>%LocalAppData%/Concierge/sessions</c>.
    /// </summary>
    public string? SessionSnapshotPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Concierge",
        "sessions",
        "active.session");
}
