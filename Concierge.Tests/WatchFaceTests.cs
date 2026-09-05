using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// The watch, tested.
///
/// This was written down as a gap — "Concierge.Wear has no tests, bUnit cannot
/// render an Android UI" — and left there, which was the wrong shape of
/// answer. bUnit cannot render an Activity and never will; what it could not
/// reach was the drawing, and the drawing was never the part that decided
/// anything. The decision moved to <see cref="WatchFace"/> and is ordinary C#.
///
/// It also surfaced a defect that had been sitting in the most consequential
/// screen in the product: Allow and Deny both called the same method with the
/// same argument. Two buttons, one behaviour, on the screen whose entire job
/// is telling those two apart.
/// </summary>
public sealed class WatchFaceTests
{
    private static ApprovalRequest Asking(string id, string risk = "low", int minutesAgo = 0)
        => new(id, $"Run {id}", risk, "because",
               DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    /// <summary>
    /// With nothing waiting the microphone is the whole interface. This is the
    /// state the watch is actually in today: the fake approval queue was
    /// deleted, and nothing carries a real one to the wrist yet.
    /// </summary>
    [Fact]
    public void With_nothing_waiting_the_watch_offers_to_listen()
    {
        var view = new WatchFace().Next([]);

        Assert.Equal(WatchScreen.Speak, view.Screen);
        Assert.Null(view.Waiting);
    }

    [Fact]
    public void A_null_queue_is_the_same_as_an_empty_one()
        => Assert.Equal(WatchScreen.Speak, new WatchFace().Next(null).Screen);

    /// <summary>
    /// A watch interrupting you had better be interrupting you about the thing
    /// it is holding, not offering a microphone underneath it.
    /// </summary>
    [Fact]
    public void Something_waiting_takes_the_whole_screen()
    {
        var view = new WatchFace().Next([Asking("a")]);

        Assert.Equal(WatchScreen.Decision, view.Screen);
        Assert.Equal("a", view.Waiting!.Id);
    }

    /// <summary>
    /// One at a time, oldest first. A queue on a 192dp face is not a queue,
    /// and answering the newest first leaves the oldest waiting longest.
    /// </summary>
    [Fact]
    public void The_one_that_has_waited_longest_is_asked_first()
    {
        var view = new WatchFace().Next([Asking("new"), Asking("old", minutesAgo: 10)]);

        Assert.Equal("old", view.Waiting!.Id);
    }

    [Fact]
    public void Answering_one_moves_on_to_the_next()
    {
        var face = new WatchFace();
        face.Decide("old", allowed: true);

        var view = face.Next([Asking("new"), Asking("old", minutesAgo: 10)]);

        Assert.Equal("new", view.Waiting!.Id);
    }

    [Fact]
    public void Answering_the_last_one_returns_to_the_microphone()
    {
        var face = new WatchFace();
        face.Decide("a", allowed: false);

        Assert.Equal(WatchScreen.Speak, face.Next([Asking("a")]).Screen);
    }

    /// <summary>
    /// The defect. Both buttons called Decide(id) — the watch recorded that an
    /// answer had been given and never which one, so Deny was Allow with a
    /// different colour.
    /// </summary>
    [Fact]
    public void Allow_and_deny_are_not_the_same_thing()
    {
        var face = new WatchFace();

        face.Decide("yes", allowed: true);
        face.Decide("no", allowed: false);

        Assert.True(face.Answer("yes"));
        Assert.False(face.Answer("no"));
    }

    [Fact]
    public void Something_unanswered_has_no_answer()
        => Assert.Null(new WatchFace().Answer("never-asked"));

    /// <summary>
    /// Changing your mind on the same request overwrites rather than throws.
    /// A double tap on a watch is a slip, not an error worth crashing over.
    /// </summary>
    [Fact]
    public void A_second_answer_replaces_the_first()
    {
        var face = new WatchFace();

        face.Decide("a", allowed: true);
        face.Decide("a", allowed: false);

        Assert.False(face.Answer("a"));
    }

    [Fact]
    public void An_empty_id_is_ignored_rather_than_stored()
    {
        var face = new WatchFace();

        face.Decide(string.Empty, allowed: true);

        Assert.Empty(face.Decisions);
    }

    // ── The wording, which the watch does not own ─────────────────────────

    /// <summary>
    /// Three form factors are allowed to look different and are not allowed to
    /// disagree about what the product does. The watch reads its sentence from
    /// the same place the sidebar does.
    /// </summary>
    [Theory]
    [InlineData("high")]
    [InlineData("critical")]
    public void A_far_reaching_request_says_so_on_the_wrist(string risk)
        => Assert.Equal(
            ApprovalRisk.ReachOf(risk),
            WatchFace.ReachOf(Asking("a", risk)));

    [Fact]
    public void The_reach_sentence_widens_with_the_risk()
    {
        Assert.Contains("outside this conversation", ApprovalRisk.ReachOf("high"));
        Assert.Contains("inside this workspace", ApprovalRisk.ReachOf("medium"));
        Assert.Contains("stays inside this conversation", ApprovalRisk.ReachOf("low"));
    }

    /// <summary>
    /// An unrecognised level is not treated as harmless. A risk string nobody
    /// planned for is exactly when the careful answer matters.
    /// </summary>
    [Fact]
    public void An_unknown_risk_is_not_quietly_settled()
    {
        Assert.Equal(ApprovalSeverity.Unknown, ApprovalRisk.SeverityOf("banana"));
        Assert.Equal(ApprovalSeverity.Unknown, ApprovalRisk.SeverityOf((string?)null));
    }

    [Fact]
    public void The_loud_levels_are_loud()
    {
        Assert.Equal(ApprovalSeverity.Danger, ApprovalRisk.SeverityOf("HIGH"));
        Assert.Equal(ApprovalSeverity.Caution, ApprovalRisk.SeverityOf("Medium"));
        Assert.Equal(ApprovalSeverity.Settled, ApprovalRisk.SeverityOf("low"));
    }
}
