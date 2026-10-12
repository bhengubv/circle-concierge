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
    private readonly CircleAiChatOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loadTask;

    public CircleAiChatRuntimeLoader(CircleAiChatRuntime runtime, CircleAiChatOptions options)
    {
        _runtime = runtime;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loadTask = Task.Run(LoadAndPrepareAsync, _shutdown.Token);
        return Task.CompletedTask;
    }

    private async Task LoadAndPrepareAsync()
    {
        await _runtime.LoadAsync(_shutdown.Token).ConfigureAwait(false);
        if (_options.AllowAutomaticDownload && _runtime.Selected is not null && !_shutdown.IsCancellationRequested)
            await _runtime.AcceptDownloadAsync(cancellationToken: _shutdown.Token).ConfigureAwait(false);
        else if (_options.ResumeAcceptedDownloadOnStartup && !_shutdown.IsCancellationRequested)
            await _runtime.ResumeAcceptedDownloadAsync(cancellationToken: _shutdown.Token).ConfigureAwait(false);

        // Resource capacity can change while the client remains open (for example,
        // when another task releases memory). Keep the capability feed current on
        // in-process heads without polling or reselecting a model in the UI.
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }

            _runtime.RefreshCapabilityCatalog();
        }
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
