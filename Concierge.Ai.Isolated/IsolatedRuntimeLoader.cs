using Microsoft.Extensions.Hosting;

namespace Concierge.Ai.Isolated;

/// <summary>
/// Starts the model host when the app starts.
///
/// Not an optimisation. The composer is disabled until the runtime reports
/// ready, and the runtime cannot report ready until the child has loaded, so
/// without this the first message can never be sent — the state that would
/// enable sending is only reachable by sending.
///
/// Started on a background task rather than awaited: StartAsync blocks the
/// host from finishing start-up, and loading several hundred megabytes of
/// weights would hold the window closed until it finished.
/// </summary>
internal sealed class IsolatedRuntimeLoader : IHostedService
{
    private readonly IsolatedChatRuntime _runtime;

    public IsolatedRuntimeLoader(IsolatedChatRuntime runtime) => _runtime = runtime;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _runtime.WarmUpAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The runtime records its own status; a warm-up that fails must
                // not take the host down with it.
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
