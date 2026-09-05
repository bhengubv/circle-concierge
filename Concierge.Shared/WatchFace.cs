namespace Concierge.Shared;

/// <summary>Which of the watch's two screens is showing.</summary>
public enum WatchScreen
{
    /// <summary>Something is waiting on you. Allow or Deny, nothing else.</summary>
    Decision,

    /// <summary>Nothing is waiting, so the microphone is the whole interface.</summary>
    Speak,
}

/// <summary>What the watch should be showing right now.</summary>
/// <param name="Screen">Which of the two.</param>
/// <param name="Waiting">The approval being asked about, when there is one.</param>
public sealed record WatchView(WatchScreen Screen, ApprovalRequest? Waiting);

/// <summary>
/// The watch's whole decision, without the watch.
///
/// The Wear OS head is an Android <c>Activity</c> that builds its views in
/// code, and bUnit cannot render one — which for a while was written down as
/// "Concierge.Wear has no tests" and left there. That was the wrong shape of
/// answer. What is untestable is the drawing; what actually decides anything
/// is this, and it is ordinary C#.
///
/// The rules, all of them:
///
///   * If something is waiting that has not been answered, that is the screen.
///     A watch interrupting you had better be interrupting you about the thing
///     it is holding, not offering a microphone underneath it.
///   * One at a time, oldest first. A queue on a 192dp face is not a queue,
///     it is a stack of one, and answering the newest first leaves the oldest
///     waiting longest.
///   * Otherwise, speak.
///
/// Decisions are held per session rather than persisted. A watch that
/// remembered your answer across a restart would be answering for a request
/// that no longer exists, and the transport that would carry the answer back
/// to the phone does not exist yet — which is stated plainly here rather than
/// implied by a button that appears to work.
/// </summary>
public sealed class WatchFace
{
    private readonly Dictionary<string, bool> _decided = new(StringComparer.Ordinal);

    /// <summary>What has been answered this session, and how.</summary>
    public IReadOnlyDictionary<string, bool> Decisions => _decided;

    /// <summary>
    /// What to show, given what is waiting.
    ///
    /// Oldest first, and anything already answered is behind you.
    /// </summary>
    public WatchView Next(IEnumerable<ApprovalRequest>? approvals)
    {
        var waiting = (approvals ?? [])
            .Where(a => a is not null && !_decided.ContainsKey(a.Id))
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefault();

        return waiting is null
            ? new WatchView(WatchScreen.Speak, null)
            : new WatchView(WatchScreen.Decision, waiting);
    }

    /// <summary>
    /// Records an answer. Which answer is kept, not just that one was given:
    /// Allow and Deny used to call the same method with the same argument, so
    /// the two buttons on the most consequential screen in the product did
    /// exactly the same thing. Nothing carries this to the phone yet — that is
    /// the companion transport, and it is not built — but the watch now at
    /// least knows what it was told.
    /// </summary>
    public void Decide(string id, bool allowed)
    {
        if (!string.IsNullOrEmpty(id))
        {
            _decided[id] = allowed;
        }
    }

    /// <summary>Whether an id has been answered, and how, or null if not yet.</summary>
    public bool? Answer(string id)
        => _decided.TryGetValue(id, out var allowed) ? allowed : null;

    /// <summary>
    /// What to put under the title. Shared with every other head through
    /// <see cref="ApprovalRisk"/>, so the watch cannot answer "what can this
    /// do?" differently from the phone.
    /// </summary>
    public static string ReachOf(ApprovalRequest approval) => ApprovalRisk.ReachOf(approval.Risk);
}
