namespace Concierge.Shared.Design;

/// <summary>One state the design has been in, and what happened to get there.</summary>
/// <param name="Document">The design as it stood.</param>
/// <param name="What">
/// What changed, in the words a person would use — "Added a heading", not
/// "Set(node_4f2a, text)". This is read under a picture, by somebody deciding
/// whether that is the version they liked.
/// </param>
/// <param name="At">When, so a long session can say "a minute ago".</param>
public sealed record DesignMoment(DesignDocument Document, string What, DateTimeOffset At);

/// <summary>
/// A design being worked on, and every state it has been in.
///
/// This is the piece that makes Design able to act freely.
///
/// Everywhere else in Concierge, something that acts asks first, because acting on
/// your files is hard to reverse. On a canvas that rule is exactly wrong: being
/// asked permission to try something is what makes a tool unusable for the people
/// this is for, and a child's entire method is try it and see. Prompting before
/// every change would also do the thing that ruins approvals generally — teach
/// somebody to stop reading them.
///
/// So nothing here asks, and what makes that safe is that going back is free and
/// total. Not an undo stack, which is a second structure that can disagree with
/// the document and usually does. Every state is kept, whole, and going back is
/// choosing one — which is why the history can be shown as pictures of how it
/// looked rather than a list of changes nobody can read.
///
/// The approvals that remain are the ones that leave: writing the design to a file
/// is <c>write_file</c> and asks, and sending it anywhere leaves the device and
/// asks. Those are unchanged, because they are not the canvas.
/// </summary>
public sealed class DesignSession
{
    /// <summary>
    /// How many states to keep.
    ///
    /// Thirty is more than anybody scrolls back through and small enough that a
    /// long afternoon does not accumulate a thousand copies of a document. The
    /// oldest goes first — the thing you want back is nearly always recent, and
    /// the very first state is kept regardless so "start again" always works.
    /// </summary>
    public const int MaxMoments = 30;

    private readonly List<DesignMoment> _moments = new();

    public DesignSession(string look = DesignLooks.Default)
    {
        _moments.Add(new DesignMoment(DesignDocument.Blank(look), "Started", DateTimeOffset.UtcNow));
        Position = 0;
    }

    /// <summary>Which state is showing.</summary>
    public int Position { get; private set; }

    /// <summary>Everything it has looked like, oldest first.</summary>
    public IReadOnlyList<DesignMoment> Moments => _moments;

    /// <summary>The design as it stands.</summary>
    public DesignDocument Current => _moments[Position].Document;

    /// <summary>What the person last pointed at, if anything.</summary>
    public string? Selected { get; private set; }

    /// <summary>Raised whenever the canvas should be redrawn.</summary>
    public event EventHandler? Changed;

    public bool CanGoBack => Position > 0;

    public bool CanGoForward => Position < _moments.Count - 1;

    /// <summary>
    /// Records a change.
    ///
    /// Changing something after going back throws away the states that came after,
    /// which is what every editor does and what everybody expects: you went back
    /// and took a different turn, so the road you did not take is gone. The
    /// alternative — keeping both — is a tree, and nobody has ever wanted to
    /// navigate one of those to get a poster finished.
    /// </summary>
    public void Record(DesignDocument document, string what)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (CanGoForward)
        {
            _moments.RemoveRange(Position + 1, _moments.Count - Position - 1);
        }

        _moments.Add(new DesignMoment(document, what, DateTimeOffset.UtcNow));

        // The first is kept whatever happens, so "back to the beginning" is always
        // reachable however long the session runs.
        while (_moments.Count > MaxMoments)
        {
            _moments.RemoveAt(1);
        }

        Position = _moments.Count - 1;

        // Pointing at something that has just been deleted would leave a selection
        // nothing on screen corresponds to, and the next sentence would have a
        // subject that is not there.
        if (Selected is not null && document.Find(Selected) is null)
        {
            Selected = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The state before this one.</summary>
    public void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        GoTo(Position - 1);
    }

    /// <summary>The state after this one, if you went back too far.</summary>
    public void Forward()
    {
        if (!CanGoForward)
        {
            return;
        }

        GoTo(Position + 1);
    }

    /// <summary>
    /// Goes to a state by picking it — which is what the history strip does when
    /// somebody taps a picture of how it looked.
    /// </summary>
    public void GoTo(int position)
    {
        if (position < 0 || position >= _moments.Count || position == Position)
        {
            return;
        }

        Position = position;

        if (Selected is not null && Current.Find(Selected) is null)
        {
            Selected = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Points at something, so the next sentence has a subject.
    ///
    /// Pointing is a first-class way to edit here, not a fallback for when
    /// describing fails. "Make this bigger" while touching the thing is how people
    /// actually talk about what is in front of them, and it removes the hardest
    /// part of correcting a design — saying which bit you mean.
    /// </summary>
    public void Point(string? nodeId)
    {
        Selected = nodeId is not null && Current.Find(nodeId) is not null ? nodeId : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What was pointed at, if it is still there.</summary>
    public DesignNode? Pointed => Current.Find(Selected);

    /// <summary>The current state as HTML, with whatever is pointed at marked.</summary>
    public string Html() => DesignRenderer.ToHtml(Current, Selected);

    /// <summary>
    /// One past state as HTML, for the history strip.
    ///
    /// Rendered without a selection: a thumbnail of how it looked should show how
    /// it looked, not what happened to be pointed at while it did.
    /// </summary>
    public string HtmlAt(int position)
        => position >= 0 && position < _moments.Count
            ? DesignRenderer.ToHtml(_moments[position].Document)
            : string.Empty;
}
