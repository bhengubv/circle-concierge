using System.Collections.Concurrent;

namespace Concierge.Shared.Tools;

/// <summary>One request waiting for a person.</summary>
/// <param name="Id">Identifies this request when the answer comes back.</param>
/// <param name="Request">What is being asked.</param>
/// <param name="AskedAt">When it was raised, so a surface can show how long it has waited.</param>
public sealed record PendingApproval(Guid Id, ToolApprovalRequest Request, DateTimeOffset AskedAt);

/// <summary>
/// Holds a tool call until a person answers it.
/// </summary>
/// <remarks>
/// <para>
/// The piece that makes the tools real. Until a host registered one of these, every write and
/// every command was refused by <see cref="UnavailableToolApprovalService"/> — the model was
/// offered tools that could only ever say no.
/// </para>
/// <para>
/// Deliberately knows nothing about how the question is asked. A MAUI dialog, a web prompt
/// and a console question all consume the same queue, and the tool layer never learns which.
/// </para>
/// <para>
/// Three things end a wait besides an answer: the turn being cancelled, the app shutting
/// down, and nobody answering for long enough. All three answer
/// <see cref="ToolApprovalDecision.Unavailable"/>, because a person who has put the phone
/// down has not consented to anything.
/// </para>
/// </remarks>
public sealed class InteractiveToolApprovalService : IToolApprovalService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Waiting> _waiting = new();
    private readonly TimeSpan _patience;
    private bool _disposed;

    /// <param name="patience">
    /// How long to wait for an answer. Generous by default — a person may be reading a diff —
    /// but not unbounded, because a forgotten prompt would hold a turn open indefinitely.
    /// </param>
    public InteractiveToolApprovalService(TimeSpan? patience = null)
        => _patience = patience ?? TimeSpan.FromMinutes(5);

    /// <summary>Raised whenever the queue changes, so a surface can redraw.</summary>
    public event EventHandler? PendingChanged;

    /// <summary>
    /// When true, every request is allowed without anybody being asked.
    ///
    /// Set by the workspace when the permission mode is Act freely. It lives
    /// here rather than in the tool loop because the tools ask for themselves —
    /// write_file and run_command call the approver directly — so this is the
    /// one place that can answer for all of them, local and remote alike.
    ///
    /// A property rather than a constructor argument because it is a decision
    /// a person changes mid-conversation, and a false default because the
    /// careful answer is the one that should survive a wiring mistake.
    /// </summary>
    public bool AllowWithoutAsking { get; set; }

    /// <summary>What is waiting, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Pending =>
        _waiting.Values
            .Select(entry => entry.Approval)
            .OrderBy(approval => approval.AskedAt)
            .ToList();

    /// <inheritdoc />
    public async ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_disposed)
        {
            return ToolApprovalDecision.Unavailable;
        }

        if (AllowWithoutAsking)
        {
            // Nothing is queued and nobody is interrupted. The call still went
            // through the approval seam, so a surface counting what ran can
            // still see it — it is simply answered immediately.
            return ToolApprovalDecision.Allowed;
        }

        var id = Guid.NewGuid();
        var entry = new Waiting(new PendingApproval(id, request, DateTimeOffset.UtcNow));
        _waiting[id] = entry;
        PendingChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            using var registration = cancellationToken.Register(
                () => entry.Answered.TrySetResult(ToolApprovalDecision.Unavailable));

            var settled = await Task.WhenAny(entry.Answered.Task, Task.Delay(_patience, CancellationToken.None))
                .ConfigureAwait(false);

            // Anything other than a person answering is not consent.
            return ReferenceEquals(settled, entry.Answered.Task)
                ? await entry.Answered.Task.ConfigureAwait(false)
                : ToolApprovalDecision.Unavailable;
        }
        finally
        {
            _waiting.TryRemove(id, out _);
            PendingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Answer one waiting request. An id that is not waiting is ignored.</summary>
    public void Answer(Guid id, ToolApprovalDecision decision)
    {
        if (_waiting.TryGetValue(id, out var entry))
        {
            entry.Answered.TrySetResult(decision);
        }
    }

    /// <summary>Refuse everything still waiting, so nothing is left blocked on shutdown.</summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (var entry in _waiting.Values)
        {
            entry.Answered.TrySetResult(ToolApprovalDecision.Unavailable);
        }
    }

    private sealed record Waiting(PendingApproval Approval)
    {
        public TaskCompletionSource<ToolApprovalDecision> Answered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
