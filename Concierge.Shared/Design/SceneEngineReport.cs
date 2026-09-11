namespace Concierge.Shared.Design;

/// <summary>
/// Whether the room was drawn by the engine or by the fallback, and why.
///
/// Space draws twice: the flat CSS room first, then three.js over the top, hiding
/// the flat one only once it has actually succeeded. That is the right way round —
/// nobody is ever looking at nothing — but it has a cost worth paying attention
/// to: **when the engine does not load, everything still works.** No error, no
/// blank screen, just a room that is a bit plainer than it should be, forever, and
/// nobody has any way to tell which they are looking at.
///
/// The engine is loaded by an absolute path from inside a srcdoc frame, which is
/// exactly the kind of thing that works on one head and quietly does not on
/// another. So the app answers the question itself rather than somebody needing to
/// look at the screen and guess.
///
/// It belongs in Engineering beside the rest of "what Concierge can do on this
/// machine", because it is a question a person actually has — *why does my room
/// look flat?* — and not only a thing a developer wants to know.
/// </summary>
public enum SceneDrawnBy
{
    /// <summary>
    /// No room has been opened yet, so nothing has been tried. Said out loud
    /// rather than guessed at: "not tried" and "tried and failed" are different
    /// answers and only one of them is a problem.
    /// </summary>
    NotTried,

    /// <summary>The engine drew it — meshes, lights, shadows.</summary>
    Engine,

    /// <summary>The flat room, which is what this machine can manage.</summary>
    Fallback,
}

/// <summary>What happened the last time a room was drawn.</summary>
/// <param name="By">Which of the two drew it.</param>
/// <param name="Why">
/// In plain words, for the fallback. Empty otherwise — a reason attached to a
/// success is a reason somebody will read as a warning.
/// </param>
/// <param name="When">When that was, so a stale answer can be seen to be stale.</param>
public sealed record SceneDrawn(SceneDrawnBy By, string Why, DateTimeOffset? When);

/// <summary>
/// Keeps the last answer. One canvas is open at a time, so one answer is the whole
/// story.
/// </summary>
public sealed class SceneEngineReport
{
    private SceneDrawn _last = new(SceneDrawnBy.NotTried, string.Empty, null);

    /// <summary>What happened last time.</summary>
    public SceneDrawn Last => _last;

    /// <summary>The room drew with the engine.</summary>
    public void Drew() => _last = new SceneDrawn(SceneDrawnBy.Engine, string.Empty, DateTimeOffset.Now);

    /// <summary>The engine did not, and this is why.</summary>
    public void FellBack(string? why)
        => _last = new SceneDrawn(
            SceneDrawnBy.Fallback,
            string.IsNullOrWhiteSpace(why) ? "The engine did not load." : why.Trim(),
            DateTimeOffset.Now);

    /// <summary>The whole thing in one sentence, for a room to print.</summary>
    public string Sentence => _last.By switch
    {
        SceneDrawnBy.Engine => "Rooms are drawn with a 3D engine — solid shapes, lights and shadows.",
        SceneDrawnBy.Fallback => $"Rooms are drawn flat, without lights or shadows. {_last.Why}",
        _ => "No room has been opened yet, so this has not been tried.",
    };
}
