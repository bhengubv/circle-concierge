using Microsoft.Extensions.Hosting;

namespace Concierge.Ai;

/// <summary>
/// Hosted service that fires the (potentially slow) model load on a background task so the
/// app starts immediately and the chat UI can render its "loading" state while CircleAI
/// downloads + opens the model. Cancellation flows through to <c>LocalModelManager</c> so
/// shutdown stops mid-download cleanly.
/// </summary>
internal sealed class CircleAiChatRuntimeLoader : IHostedService
{
    private readonly CircleAiChatRuntime _runtime;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loadTask;

    public CircleAiChatRuntimeLoader(CircleAiChatRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loadTask = Task.Run(() => _runtime.LoadAsync(_shutdown.Token), _shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        if (_loadTask is not null)
        {
            try
            {
                await _loadTask.ConfigureAwait(false);
            }
            catch
            {
                // Load failures are surfaced through IChatRuntime.StatusMessage; nothing useful
                // to add at shutdown.
            }
        }
        _shutdown.Dispose();
    }
}
