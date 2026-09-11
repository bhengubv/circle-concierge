using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Whether the room was drawn by the engine or by the fallback, and why.
///
/// The fallback is silent by design — the flat room is written first and the
/// engine hides it only once it has drawn, so a machine that cannot run the engine
/// shows a room that works perfectly and never mentions it. That is right for
/// whoever is using it and useless for whoever is wondering why theirs looks
/// flatter than somebody else's.
///
/// It matters more than it sounds because the engine is loaded by an absolute path
/// from inside a srcdoc frame — exactly the kind of thing that works on one head
/// and quietly does not on another. Without this the desktop head could have been
/// falling back for weeks and every test would still have passed.
/// </summary>
public sealed class SceneEngineReportTests
{
    /// <summary>
    /// "Not tried" and "tried and failed" are different answers, and only one of
    /// them is a problem. A report that guessed would be the same defect as an
    /// approvals badge that always said two.
    /// </summary>
    [Fact]
    public void Before_any_room_is_opened_nothing_has_been_tried()
    {
        var report = new SceneEngineReport();

        Assert.Equal(SceneDrawnBy.NotTried, report.Last.By);
        Assert.Null(report.Last.When);
        Assert.Contains("has not been tried", report.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_room_the_engine_drew_says_so()
    {
        var report = new SceneEngineReport();
        report.Drew();

        Assert.Equal(SceneDrawnBy.Engine, report.Last.By);
        Assert.NotNull(report.Last.When);
        Assert.Contains("lights and shadows", report.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A reason attached to a success is a reason somebody reads as a warning.
    /// </summary>
    [Fact]
    public void A_room_the_engine_drew_carries_no_excuse()
        => Assert.Equal(string.Empty, Reported(r => r.Drew()).Why);

    [Fact]
    public void A_flat_room_says_why()
    {
        var last = Reported(r => r.FellBack("This machine has no working WebGL."));

        Assert.Equal(SceneDrawnBy.Fallback, last.By);
        Assert.Equal("This machine has no working WebGL.", last.Why);
    }

    /// <summary>
    /// "It fell back and I do not know why" is still worth saying. A blank where a
    /// reason should be reads as though nothing happened.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_flat_room_with_no_reason_given_still_says_something(string? why)
        => Assert.False(string.IsNullOrWhiteSpace(Reported(r => r.FellBack(why)).Why));

    [Fact]
    public void The_reason_is_shown_to_somebody_reading_the_room()
        => Assert.Contains(
            "no working WebGL",
            Report(r => r.FellBack("This machine has no working WebGL.")).Sentence,
            StringComparison.Ordinal);

    /// <summary>
    /// One canvas is open at a time, so the last answer is the whole story — and a
    /// machine that failed once and works now must not go on saying it failed.
    /// </summary>
    [Fact]
    public void The_last_answer_replaces_the_one_before_it()
    {
        var report = new SceneEngineReport();
        report.FellBack("No engine.");
        report.Drew();

        Assert.Equal(SceneDrawnBy.Engine, report.Last.By);
        Assert.Equal(string.Empty, report.Last.Why);
    }

    // ── What the page actually calls back with ────────────────────────────

    /// <summary>
    /// Every way the engine can fail reports one, and the success reports one.
    /// A path that returns without saying anything leaves the room stuck on
    /// whatever it said last, which is worse than saying nothing at all.
    /// </summary>
    [Fact]
    public void Every_way_out_of_the_drawing_says_what_happened()
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));

        var html = DesignMediums.Render(scene.Add(room)
            .Add(DesignNode.New(DesignNodeKind.Solid, room.Id, ("text", "Table"))));

        // Four ways out: unreadable room, no engine, no WebGL, a fault in ours —
        // plus the one where it works.
        Assert.Contains("report(false, 'The room could not be read.')", html, StringComparison.Ordinal);
        Assert.Contains("report(false, 'The 3D engine is not available", html, StringComparison.Ordinal);
        Assert.Contains("report(false, 'This machine has no working WebGL.')", html, StringComparison.Ordinal);
        Assert.Contains("report(false, 'The engine loaded and the room did not draw.", html, StringComparison.Ordinal);
        Assert.Contains("report(true, '')", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The frame decides asynchronously and can land either side of the bridge
    /// being attached, so the answer is both pushed and left where it can be
    /// collected. Reporting only one way is how this works on a fast machine and
    /// says "not tried yet" forever on a slow one.
    /// </summary>
    [Fact]
    public void The_answer_is_left_where_it_can_be_found_as_well_as_handed_over()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"))));

        Assert.Contains("window.__deepReport = { drew: drew, why: why }", html, StringComparison.Ordinal);
        Assert.Contains("if (window.__conciergeDrew) { window.__conciergeDrew(drew, why); }", html, StringComparison.Ordinal);
    }

    private static SceneEngineReport Report(Action<SceneEngineReport> what)
    {
        var report = new SceneEngineReport();
        what(report);
        return report;
    }

    private static SceneDrawn Reported(Action<SceneEngineReport> what) => Report(what).Last;
}
