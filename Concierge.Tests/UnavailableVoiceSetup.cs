using Concierge.Shared.Chat;
using Concierge.Shared.Media;

namespace Concierge.Tests;

/// <summary>Settings fixtures have no installed speech models or download service.</summary>
internal sealed class UnavailableVoiceSetup : ILocalVoiceModelSetup
{
    public LocalVoiceSetupStatus GetStatus()
        => new(false, false, false, false, "Voice is unavailable in this test.");

    public Task<LocalVoiceSetupStatus> PrepareAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(GetStatus());
}
