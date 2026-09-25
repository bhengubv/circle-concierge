namespace Concierge.Away;

/// <summary>
/// What the face should show after something happened.
/// </summary>
/// <param name="Say">
/// The one line, or null to leave whatever is there. A watch has room for a sentence, and
/// the sentence is the whole answer.
/// </param>
/// <param name="Waiting">What is waiting on a person, as the desk last reported it.</param>
/// <param name="Changed">What changed last, or null when nothing has.</param>
public sealed record WatchUpdate(
    string? Say,
    IReadOnlyList<AwayWaiting> Waiting,
    AwayChanged? Changed);

/// <summary>
/// The watch's doing, without the watch.
///
/// **`WatchFace` already made this argument once and it was only half-applied.** It holds
/// which screen shows, because that is a decision and decisions are ordinary C# — while
/// sending, flushing, answering and undoing stayed inside the Android `Activity`, where bUnit
/// cannot reach them and neither can anything else. So five methods that decide what a person
/// reads were untestable, and "the watch needs hardware to prove" was doing a lot of work it
/// had not earned: Android's recogniser is hardware, and none of this is.
///
/// What is left in the Activity after this is drawing and one call per button.
/// </summary>
/// <remarks>
/// It returns words rather than setting them, so nothing here touches a UI thread and every
/// sentence a wrist can show is a value a test can read.
/// </remarks>
public sealed class WatchHands(AwayClient desk)
{
    /// <summary>
    /// Say something, and hear what to put on the face.
    ///
    /// The reply is used exactly as it came back. **The one thing this screen must never do is
    /// look the same whether it worked or not** — which is why a kept sentence, a partial
    /// delivery and a real answer all read differently.
    /// </summary>
    public async Task<WatchUpdate> SaidAsync(
        string said, Situation situation, CancellationToken cancellationToken = default)
    {
        var answer = await desk.SayAsync(said, situation, cancellationToken).ConfigureAwait(false);

        var say = answer.Reply
            ?? (answer.Understood ? answer.What ?? "Done." : "Nothing came back.");

        return await ThenAsync(say, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answer something that was waiting.
    ///
    /// A false answer means it was no longer waiting — a turn that gave up while somebody
    /// walked to the kitchen. Said out loud, because a watch reporting "allowed" for a call
    /// that never ran is the approvals badge that always said two, on the one screen with
    /// room for a single sentence.
    /// </summary>
    public async Task<WatchUpdate> AnsweredAsync(
        Guid id, bool allowed, CancellationToken cancellationToken = default)
    {
        var landed = await desk.AnswerAsync(id, allowed, cancellationToken).ConfigureAwait(false);

        return await ThenAsync(landed ? null : "That one had already gone.", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Take the last change back.
    ///
    /// Null from the desk means there was nothing behind it, and that is said rather than
    /// leaving a face that looks identical whether the change went back or did not.
    /// </summary>
    public async Task<WatchUpdate> TookItBackAsync(CancellationToken cancellationToken = default)
    {
        var now = await desk.UndoAsync(cancellationToken).ConfigureAwait(false);

        return new WatchUpdate(
            now is null ? "There was nothing to go back to." : now.What,
            await desk.WaitingAsync(cancellationToken).ConfigureAwait(false),
            null);
    }

    /// <summary>
    /// The face came on. Deliver anything held, then read what is true now.
    ///
    /// **Held sentences go first.** A wrist is out of range constantly, and the moment the
    /// screen lights is the moment worth trying again — before anybody has to think about it.
    /// </summary>
    public async Task<WatchUpdate> LookedAgainAsync(CancellationToken cancellationToken = default)
    {
        var sent = await desk.FlushAsync(cancellationToken).ConfigureAwait(false);
        var left = desk.Waiting;

        // Some going and some not says both. Delivery stops at the first one that will not
        // go, so a network coming back patchily leaves part of the queue behind — the
        // ordinary outcome, and a face reading "Sent 2" with one still on the wrist is
        // somebody believing their morning's work arrived.
        var say = (sent, left) switch
        {
            (0, _) => null,
            (_, 0) => sent == 1 ? "Sent what was waiting." : $"Sent {sent} that were waiting.",
            _ => $"Sent {sent}, {left} still waiting.",
        };

        return await ThenAsync(say, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whatever was said, plus what is true now.</summary>
    private async Task<WatchUpdate> ThenAsync(string? say, CancellationToken cancellationToken)
        => new(
            say,
            await desk.WaitingAsync(cancellationToken).ConfigureAwait(false),
            await desk.LastChangeAsync(cancellationToken).ConfigureAwait(false));
}
