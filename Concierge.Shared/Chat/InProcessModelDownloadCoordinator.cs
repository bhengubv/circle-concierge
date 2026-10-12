namespace Concierge.Shared.Chat;

/// <summary>
/// Starts a runtime's accepted download away from the caller's synchronization context.
/// Platform heads can replace this with an OS-owned job or service.
/// </summary>
public sealed class InProcessModelDownloadCoordinator : IModelDownloadCoordinator
{
    public Task<bool> StartAcceptedDownloadAsync(
        IModelDownloadRequired runtime,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return Task.Run(
            () => runtime.AcceptDownloadAsync(progress, cancellationToken),
            CancellationToken.None);
    }

    public Task StopAsync(IModelDownloadRequired runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.CancelCurrentDownloadAsync();
    }
}
