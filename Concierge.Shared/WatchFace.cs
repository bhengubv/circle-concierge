namespace Concierge.Shared;

/// <summary>Which of the watch's two screens is showing.</summary>
public enum WatchScreen
{
    /// <summary>Something is waiting on you. Allow or Deny, nothing else.</summary>
    Decision,

    /// <summary>Nothing is waiting, so the microphone is the whole interface.</summary>
    Speak,

    /// <summary>Something changed and has not been looked at. What it was, and a way to say no.</summary>
    Change,
}

/// <summary>
/// Whether a surface belongs on a watch at all.
///
/// Written down as a rule because the alternative is each head deciding for
/// itself, and the failure mode of that is a canvas rendered onto a 192dp face
/// because nothing said not to.
/// </summary>
public static class WatchSurfaces
{
    /// <summary>
    /// The design canvas does not go on a watch, and the reason is not screen size.
    ///
    /// **Changed to true on 2026-09-15, deliberately, which is what this constant was for.**
    /// The blocker recorded below was that the correction had no way home — approvals needed
    /// the mesh, and the mesh cannot sign a packet. That was answered a different way: the
    /// wrist reaches the desk over the ordinary network, guarded by the key the web head
    /// already had, and it already answers approvals over it.
    ///
    /// The screen is the one this comment named in its last paragraph, unchanged: the last
    /// change plus undo. Not a canvas. The reasoning below stands as written and is kept,
    /// because the decision it records is the reason the screen is this small.
    ///
    /// A watch could carry the useful half of designing — "no, not like that" —
    /// without carrying the canvas. Glancing at your wrist, seeing what changed and
    /// saying no is a better fit for the product's actual claim than any attempt to
    /// draw on it would be.
    ///
    /// It is not built because the correction has no way home. Answering on the
    /// wrist needs approvals to ride the mesh, and that is held behind packet
    /// signature verification — putting it on an unauthenticated channel would let
    /// anyone on the café wifi say "allowed". So a design screen today would be a
    /// button that appears to work and does not, which is the one thing this file
    /// already refuses to ship: its own decisions are held per session rather than
    /// persisted, for exactly that reason.
    ///
    /// When the transport lands, the screen to build is the last change plus undo.
    /// Not a canvas. Deciding that now is cheaper than deciding it under pressure.
    /// </summary>
    public const bool Design = true;

    /// <summary>
    /// And what it is, so nobody builds the other thing.
    ///
    /// The last change and a way to take it back. Not a canvas, not a moments strip, not a
    /// picker — one sentence saying what happened and one button saying no. Everything a
    /// person does on a wrist is done while the other arm is holding something.
    /// </summary>
    public const string DesignIs = "The last change, and undo.";
}

/// <summary>What the watch should be showing right now.</summary>
/// <param name="Screen">Which of the three.</param>
/// <param name="Waiting">The approval being asked about, when there is one.</param>
/// <param name="Changed">What changed, when that is the screen.</param>
public sealed record WatchView(WatchScreen Screen, ApprovalRequest? Waiting, string? Changed = null);

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
        => Next(approvals, null);

    /// <summary>
    /// What to show, given what is waiting and what changed.
    ///
    /// Order matters and is the whole of the rule: **something waiting on you beats something
    /// that already happened.** An approval is holding a turn open; a change has been made and
    /// will still be there in a minute. A watch interrupting you had better be interrupting
    /// about the thing it is holding.
    ///
    /// A change is shown once. Seen, it goes away and the microphone comes back — a wrist that
    /// kept showing the same sentence every time you raised it would be a notification that
    /// does not clear, which is the thing everybody turns off first.
    /// </summary>
    public WatchView Next(IEnumerable<ApprovalRequest>? approvals, string? changed)
    {
        var waiting = (approvals ?? [])
            .Where(a => a is not null && !_decided.ContainsKey(a.Id))
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefault();

        if (waiting is not null)
        {
            return new WatchView(WatchScreen.Decision, waiting, null);
        }

        return !string.IsNullOrWhiteSpace(changed) && !string.Equals(changed, _seen, StringComparison.Ordinal)
            ? new WatchView(WatchScreen.Change, null, changed)
            : new WatchView(WatchScreen.Speak, null, null);
    }

    /// <summary>
    /// That change has been looked at, so the face goes back to the microphone.
    ///
    /// Held by what it was rather than by a flag: the next change says something different
    /// and shows itself, and the same change arriving twice — a screen coming back on, a
    /// reconnect — does not.
    /// </summary>
    public void Seen(string? changed) => _seen = changed;

    private string? _seen;

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
