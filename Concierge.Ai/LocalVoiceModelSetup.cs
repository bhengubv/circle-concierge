using Concierge.Shared.Chat;
using Concierge.Shared.Media;

namespace Concierge.Ai;

/// <summary>Downloads the small speech models into the same private model folder as Concierge.</summary>
public sealed class LocalVoiceModelSetup(
    LocalVoiceOptions options,
    CircleAiVoiceRuntime runtime) : ILocalVoiceModelSetup
{
    private readonly SemaphoreSlim _downloads = new(1, 1);

    public LocalVoiceSetupStatus GetStatus()
    {
        var files = new VoiceModels(options.ModelsDirectory).Look();
        return new LocalVoiceSetupStatus(
            files.Listener is not null,
            files.Speaker is not null,
            runtime.SupportsTranscription,
            runtime.SupportsSynthesis,
            runtime.SupportsTranscription
                ? "Offline voice input is ready on this device."
                : files.Listener is null
                    ? "Offline voice models are not downloaded. Setup downloads about 110 MB to this device."
                    : "The listening model is present, but this device's local transcription runtime is not ready.");
    }

    public async Task<LocalVoiceSetupStatus> PrepareAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await new VoiceModels(options.ModelsDirectory)
                .FetchAsync(progress, cancellationToken)
                .ConfigureAwait(false);

            return GetStatus();
        }
        finally
        {
            _downloads.Release();
        }
    }
}
