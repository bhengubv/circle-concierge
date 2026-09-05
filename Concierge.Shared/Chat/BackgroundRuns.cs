using System.Collections.Concurrent;

namespace Concierge.Shared.Chat;

/// <summary>
/// Which conversations are still working.
///
/// A turn used to stop the moment you left the thread, and for a good reason:
/// the generator was in-process, and a native fault in it while the component
/// it wrote into was gone took the whole application with it. Cancelling was
/// the only safe answer available.
///
/// That reason is gone. The model runs in a child process now, so a fault
/// costs the child and nothing else — which means a long run can outlive the
/// screen that started it, and should. Asking something to read six files and
/// then losing the answer because you looked at another thread is the kind of
/// thing that teaches people not to leave the window alone.
///
/// This is deliberately only a register. It does not own the loop, schedule
/// anything, or decide when to stop — it records what is in flight so the
/// sidebar can show a thread is still working and a returning component knows
/// not to start again.
/// </summary>
public sealed class BackgroundRuns
{
    private readonly ConcurrentDictionary<Guid, RunningTurn> _running = new();

    /// <summary>Raised when a run starts or ends, so a surface can redraw.</summary>
    public event EventHandler? Changed;

    /// <summary>Conversations with a turn in flight.</summary>
    public IReadOnlyCollection<Guid> Running => _running.Keys.ToList();

    public bool IsRunning(Guid conversationId) => _running.ContainsKey(conversationId);

    /// <summary>How long it has been going, for a surface that wants to say.</summary>
    public DateTimeOffset? StartedAt(Guid conversationId)
        => _running.TryGetValue(conversationId, out var run) ? run.StartedAt : null;

    /// <summary>
    /// Marks a turn as in flight. The cancellation source is kept so a person
    /// can stop a run from a thread other than the one that started it.
    /// </summary>
    public void Started(Guid conversationId, CancellationTokenSource cancellation)
    {
        _running[conversationId] = new RunningTurn(DateTimeOffset.UtcNow, cancellation);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Finished(Guid conversationId)
    {
        if (_running.TryRemove(conversationId, out _))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Stops one, from anywhere. Cancelling a run you cannot see is the other
    /// half of letting it keep going when you leave: without this, a run you
    /// walked away from could only be stopped by walking back.
    /// </summary>
    public void Stop(Guid conversationId)
    {
        if (!_running.TryGetValue(conversationId, out var run))
        {
            return;
        }

        try
        {
            run.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished between the look-up and here.
        }
    }

    /// <summary>Stops everything. Used when the app is closing.</summary>
    public void StopAll()
    {
        foreach (var id in _running.Keys.ToList())
        {
            Stop(id);
        }
    }

    private sealed record RunningTurn(DateTimeOffset StartedAt, CancellationTokenSource Cancellation);
}
