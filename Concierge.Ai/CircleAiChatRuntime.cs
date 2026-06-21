using System.Runtime.CompilerServices;
using CircleAI.Core;
using CircleAI.Inference;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging;

namespace Concierge.Ai;

/// <summary>
/// <see cref="IChatRuntime"/> backed by CircleAI's on-device chat generator (MNN-LLM
/// running a Qwen / Kimi family model). Model selection is device-driven via
/// <see cref="ModelSelector.SelectForCurrentDevice"/> — neither the host nor the UI
/// picks; whatever tier fits the device's RAM is what loads.
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
public sealed class CircleAiChatRuntime : IChatRuntime, IPersistableChatRuntime, IAsyncDisposable
{
    private readonly ILogger<CircleAiChatRuntime> _logger;
    private readonly CircleAiChatOptions _options;
    private readonly TaskCompletionSource<IChatGenerator?> _generatorReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _statusGate = new();
    // Single in-flight generation at a time — MNN model handles are not
    // re-entrant, and the Razor UI never opens more than one stream anyway.
    // SaveSessionAsync / LoadSessionAsync also acquire this so a snapshot
    // never races with a partial decode.
    private readonly SemaphoreSlim _generationGate = new(1, 1);

    private string _statusMessage = "Engine queued for load…";
    private string _engineLabel = "CircleAI (pending)";
    private bool _isReady;

    public CircleAiChatRuntime(ILogger<CircleAiChatRuntime> logger, CircleAiChatOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public string Id => "circleai";
    public string EngineLabel => _engineLabel;
    public bool IsReady => _isReady;
    public string StatusMessage => _statusMessage;
    public string? SessionSnapshotPath => _options.SessionSnapshotPath;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            SetStatus("Picking model for this device…", ready: false);
            var tier = ModelSelector.SelectForCurrentDevice();
            _engineLabel = $"{tier.ModelId} (CircleAI)";

            SetStatus($"Resolving model path for {tier.ModelId}…", ready: false);
            var modelsDirectory = _options.ModelsDirectory;
            Directory.CreateDirectory(modelsDirectory);
            using var manager = _options.RepositoryUrl is null
                ? new LocalModelManager(modelRepositoryUrl: null, modelsDirectory)
                : new LocalModelManager(_options.RepositoryUrl, modelsDirectory);
            var modelPath = await manager.GetModelPathAsync(tier.ModelId, ct: cancellationToken).ConfigureAwait(false);

            SetStatus($"Loading {tier.ModelId} into memory (one-time)…", ready: false);
            var generator = new QwenTextGenerator(modelPath, _options.ContextSize);

            SetStatus($"Ready · {tier.ModelId}", ready: true);
            _generatorReady.TrySetResult(generator);
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

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var generator = await _generatorReady.Task.ConfigureAwait(false);
        if (generator is null)
        {
            yield return $"[{StatusMessage}]";
            yield break;
        }

        var translated = messages
            .Select(turn => new ChatMessage(turn.Role, turn.Content))
            .ToList();

        // Per-call generation knobs:
        //   * Budget = Normal — the runtime caps tokens at ~512, uses TQ4 KV
        //     compression, and automatically downgrades to Low when battery
        //     drops below 15%. Matches Concierge's "snappy chat reply" shape;
        //     callers that need long planning replies can override per-message
        //     once that surface exists.
        //   * UsePrefixCache = true — first conversation per (modelId,
        //     systemPrompt) snapshots the prefill KV; subsequent ones reuse
        //     it so first-token latency drops to sub-200ms (RT-06).
        var options = new GenerationOptions
        {
            Budget = PowerBudget.Normal,
            UsePrefixCache = true,
            MaxTokens = (int)_options.MaxOutputTokens,
        };

        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

        var generator = _generatorReady.Task.IsCompletedSuccessfully
            ? await _generatorReady.Task.ConfigureAwait(false)
            : null;
        if (generator is null)
        {
            // Engine never loaded — nothing to snapshot. Not an error from the
            // host's perspective; OnSleep happens whether or not the user has
            // typed anything.
            return false;
        }

        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

        var generator = await _generatorReady.Task.ConfigureAwait(false);
        if (generator is null)
        {
            return false;
        }

        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (_generatorReady.Task.IsCompletedSuccessfully)
        {
            var generator = await _generatorReady.Task.ConfigureAwait(false);
            generator?.Dispose();
        }
        _generationGate.Dispose();
    }

    private void SetStatus(string message, bool ready)
    {
        lock (_statusGate)
        {
            _statusMessage = message;
            _isReady = ready;
        }
    }
}

/// <summary>
/// Construction-time knobs for <see cref="CircleAiChatRuntime"/>. Defaults assume the
/// model store lives under <c>%LocalAppData%/Concierge/models</c> and that downloads,
/// when needed, come from CircleAI's default ModelScope source.
/// </summary>
public sealed class CircleAiChatOptions
{
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
