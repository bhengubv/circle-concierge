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
/// The model load is potentially slow (download + native open + warm-up). A background
/// <see cref="CircleAiChatRuntimeLoader"/> hosted service kicks it off at app start and
/// drives <see cref="IsReady"/> / <see cref="StatusMessage"/>. Callers that hit
/// <see cref="StreamAsync"/> before the load completes block on the same task — they
/// don't race the loader.
/// </remarks>
public sealed class CircleAiChatRuntime : IChatRuntime, IAsyncDisposable
{
    private readonly ILogger<CircleAiChatRuntime> _logger;
    private readonly CircleAiChatOptions _options;
    private readonly TaskCompletionSource<IChatGenerator?> _generatorReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _statusGate = new();

    private string _statusMessage = "Engine queued for load…";
    private string _engineLabel = "CircleAI (pending)";
    private bool _isReady;

    public CircleAiChatRuntime(ILogger<CircleAiChatRuntime> logger, CircleAiChatOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public string EngineLabel => _engineLabel;
    public bool IsReady => _isReady;
    public string StatusMessage => _statusMessage;

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

        await foreach (var chunk in generator.StreamAsync(translated, ct: cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_generatorReady.Task.IsCompletedSuccessfully)
        {
            var generator = await _generatorReady.Task.ConfigureAwait(false);
            generator?.Dispose();
        }
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
}
