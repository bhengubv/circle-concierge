using Concierge.Shared.Chat;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Concierge.Shared.Settings;

/// <summary>Executes due work through the same durable conversation/approval path as chat.</summary>
public sealed class ScheduledWorkService(IScheduledTaskStore store, IServiceScopeFactory scopes) : BackgroundService
{
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    public string Status { get; private set; } = "Scheduled work runs while Concierge is open. Overdue work resumes when it opens.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await RunDueAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Status = $"Scheduled work needs attention: {ex.Message}"; }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!await _tick.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            using var lease = store.TryAcquireExecutionLease();
            if (lease is null) return;
            foreach (var due in store.Due(now))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var scope = scopes.CreateScope();
                var runtime = ConciergeRuntimePreference.Choose(scope.ServiceProvider.GetServices<IChatRuntime>());
                if (runtime is null || (!runtime.IsReady && runtime is not IChatRuntimePreparation { CanWaitForPreparation: true }))
                { Status = "Scheduled work is waiting for Circle AI to be available."; return; }
                var task = store.Begin(due.Id, now);
                if (task is null) continue;
                using var runStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _active[task.Id] = runStop;
                try
                {
                    if (!store.All().Any(t => t.Id == task.Id && t.Enabled)) runStop.Cancel();
                    runStop.Token.ThrowIfCancellationRequested();
                    var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
                    if (await conversations.GetAsync(task.ConversationId!.Value, cancellationToken).ConfigureAwait(false) is null)
                        await conversations.StartWithIdAsync(task.ConversationId.Value, "local", $"Scheduled: {task.Description[..Math.Min(80, task.Description.Length)]}", cancellationToken: cancellationToken).ConfigureAwait(false);
                    // Recovered attempts reuse their durable RunId. TurnRunner replays an
                    // existing receipt, or reports an interrupted run without executing it again.
                    using var approval = scope.ServiceProvider.GetService<InteractiveToolApprovalService>()
                        ?.ForTurn(task.PermissionMode == ToolPermissionMode.ActFreely);
                    Status = $"Running: {task.Description}";
                    var result = await scope.ServiceProvider.GetRequiredService<TurnRunner>().RunAsync(
                        new TurnRequest(task.ConversationId.Value, task.Description, runtime,
                            Settings: new TurnSettings(ExecuteTools: task.PermissionMode.RunsTools(), PermissionMode: task.PermissionMode),
                            RunId: task.RunId), cancellationToken: runStop.Token).ConfigureAwait(false);
                    var state = result.Stopped is not null ? "Needs attention"
                        : string.IsNullOrWhiteSpace(result.Text) ? "Needs attention" : "Completed";
                    store.Finish(task.Id, DateTimeOffset.UtcNow, state, result.Text,
                        result.Stopped ?? (state == "Completed" ? null : "No answer was returned. Open the conversation before retrying."));
                }
                catch (OperationCanceledException)
                {
                    store.Finish(task.Id, DateTimeOffset.UtcNow, "Stopped", null, "The run stopped. Review its conversation before retrying.");
                    if (cancellationToken.IsCancellationRequested) throw;
                }
                catch (Exception ex)
                {
                    store.Finish(task.Id, DateTimeOffset.UtcNow, "Needs attention", null, ex.Message);
                }
                finally { _active.TryRemove(task.Id, out _); }
            }
            Status = "Scheduled work runs while Concierge is open. Overdue work resumes when it opens.";
        }
        finally { _tick.Release(); }
    }

    public void Stop(Guid id)
    {
        store.SetEnabled(id, false);
        if (_active.TryGetValue(id, out var cancellation))
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
