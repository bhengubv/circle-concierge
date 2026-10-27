using Concierge.Shared.Chat;

namespace Concierge.Shared.Media;

/// <summary>Voice model presence and the device's actual voice capabilities.</summary>
public sealed record LocalVoiceSetupStatus(
    bool ListenerInstalled,
    bool SpeakerInstalled,
    bool CanTranscribe,
    bool CanSpeak,
    string Message);

/// <summary>Explicit, observable setup for speech models stored on this device.</summary>
public interface ILocalVoiceModelSetup
{
    LocalVoiceSetupStatus GetStatus();

    Task<LocalVoiceSetupStatus> PrepareAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
