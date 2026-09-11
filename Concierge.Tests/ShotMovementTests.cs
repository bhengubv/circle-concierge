using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// How a shot moves while it is on screen.
///
/// **A still picture held for four seconds looks like a fault**, and that is the whole of
/// what a motion-graphics engine is wanted for here. A slow push in or a slow drift across
/// turns a card or a photograph into a shot, and both are ordinary filters the encoder
/// already has — no composition engine, no keyframes, no curve editor, which are the three
/// things that make this category's software unusable for the people this is for.
///
/// The export tests that actually run the encoder live in the media export suite. These are
/// about the decision: which words are honoured, which are refused, and that the preview and
/// the export agree about them — two places knowing the same words is how they drift.
/// </summary>
public sealed class ShotMovementTests
{
    private static DesignNode Shot(string move, string? footage = null)
        => DesignNode.New(
            DesignNodeKind.Frame,
            null,
            footage is null
                ? [("text", "A shot"), ("move", move)]
                : [("text", "A shot"), ("move", move), ("src", footage)]);

    private static DesignWorkbench Open()
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Motion), "Made it");

        return bench;
    }

    private static IAgentTool Move(DesignWorkbench bench)
        => new DesignToolSource(bench).Tools.Single(tool => tool.Name == "design_move");

    // ── What the encoder is asked to do ───────────────────────────────────

    [Fact]
    public void A_shot_that_says_nothing_moves_not_at_all()
        => Assert.Equal(
            string.Empty,
            FfmpegMediaExport.MoveOf(DesignNode.New(DesignNodeKind.Frame, null, ("text", "x")), 3, true));

    [Fact]
    public void A_fade_works_on_a_still_and_on_footage_alike()
    {
        Assert.Contains("fade=t=in", FfmpegMediaExport.MoveOf(Shot("fade"), 3, still: true), StringComparison.Ordinal);
        Assert.Contains("fade=t=in", FfmpegMediaExport.MoveOf(Shot("fade"), 3, still: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("grow")]
    [InlineData("drift")]
    public void A_push_or_a_pan_is_asked_for_only_where_there_is_a_still_to_do_it_to(string move)
    {
        Assert.Contains("zoompan", FfmpegMediaExport.MoveOf(Shot(move), 3, still: true), StringComparison.Ordinal);

        // On real footage it fights a picture that is already moving, costs time, and looks
        // worse — so nothing is asked for rather than something being done badly.
        Assert.Equal(string.Empty, FfmpegMediaExport.MoveOf(Shot(move), 3, still: false));
    }

    /// <summary>
    /// zoompan counts frames, not seconds, and the export normalises everything to thirty a
    /// second. A movement spread over the wrong number of frames stops early and holds.
    /// </summary>
    [Fact]
    public void The_movement_is_spread_over_the_length_the_shot_is_actually_held()
    {
        Assert.Contains("d=120", FfmpegMediaExport.MoveOf(Shot("grow"), 4, still: true), StringComparison.Ordinal);
        Assert.Contains("d=30", FfmpegMediaExport.MoveOf(Shot("grow"), 1, still: true), StringComparison.Ordinal);
    }

    /// <summary>
    /// A model inventing "cinematic" should cost a shot its movement, not the whole film.
    /// </summary>
    [Fact]
    public void A_word_nobody_knows_moves_nothing_rather_than_failing_the_export()
        => Assert.Equal(string.Empty, FfmpegMediaExport.MoveOf(Shot("cinematic"), 3, still: true));

    [Fact]
    public void Every_word_it_offers_is_a_word_it_honours()
    {
        foreach (var move in FfmpegMediaExport.Moves.Where(word => word != "still"))
        {
            Assert.NotEqual(string.Empty, FfmpegMediaExport.MoveOf(Shot(move), 3, still: true));
        }
    }

    // ── What is on screen ─────────────────────────────────────────────────

    /// <summary>
    /// A movement that only appears in the exported file is a movement nobody can judge
    /// until it is too late to change it.
    /// </summary>
    [Theory]
    [InlineData("fade")]
    [InlineData("grow")]
    [InlineData("drift")]
    public void The_preview_shows_it_moving_too(string move)
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Motion).Add(Shot(move));

        var html = DesignMediums.Render(document);

        Assert.Contains($"moves {move}", html, StringComparison.Ordinal);
        Assert.Contains($"@keyframes {(move == "fade" ? "fadein" : move)}", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preview runs the movement over the shot's own length, so what is on screen is
    /// what will be in the file rather than a general impression of it.
    /// </summary>
    [Fact]
    public void And_over_the_same_length()
    {
        var shot = DesignNode.New(
            DesignNodeKind.Frame, null, ("text", "A shot"), ("move", "grow"), ("seconds", "6"));

        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Motion).Add(shot));

        Assert.Contains("--held:6s", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shot_that_holds_still_says_nothing_about_moving()
    {
        var html = DesignMediums.Render(
            DesignDocument.Blank(medium: DesignMedium.Motion)
                .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "A shot"))));

        // On the shot itself, not in the stylesheet: the rules for moving are always
        // written out, and asserting on the bare word catches them instead. Third time
        // this trap has been walked into in this suite.
        Assert.DoesNotContain("shot on moves", html, StringComparison.Ordinal);
    }

    // ── Saying so ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_shot_can_be_told_to_move()
    {
        var bench = Open();
        var shot = DesignNode.New(DesignNodeKind.Frame, null, ("text", "A shot"));
        bench.Session!.Record(bench.Session.Current.Add(shot), "Added a shot");

        var result = await Move(bench).InvokeAsync(
            new JsonObject { ["id"] = shot.Id, ["move"] = "grow" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal("grow", bench.Session.Current.Find(shot.Id)!.Props["move"]);
    }

    [Fact]
    public async Task And_told_to_stop()
    {
        var bench = Open();
        var shot = DesignNode.New(DesignNodeKind.Frame, null, ("text", "A shot"), ("move", "drift"));
        bench.Session!.Record(bench.Session.Current.Add(shot), "Added a shot");

        await Move(bench).InvokeAsync(new JsonObject { ["id"] = shot.Id, ["move"] = "still" });

        Assert.Equal(string.Empty, bench.Session.Current.Find(shot.Id)!.Props["move"]);
    }

    /// <summary>
    /// A movement that does nothing because nobody knows the word is a shot that looks
    /// unchanged and a person who believes it worked.
    /// </summary>
    [Fact]
    public async Task A_word_nobody_knows_is_refused_with_the_words_that_work()
    {
        var bench = Open();
        var shot = DesignNode.New(DesignNodeKind.Frame, null, ("text", "A shot"));
        bench.Session!.Record(bench.Session.Current.Add(shot), "Added a shot");

        var result = await Move(bench).InvokeAsync(
            new JsonObject { ["id"] = shot.Id, ["move"] = "cinematic" });

        Assert.False(result.Success);
        Assert.Contains("drift", result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Told "done", nobody looks at that shot again. So a word that only works on a still is
    /// refused on footage rather than accepted and quietly dropped.
    /// </summary>
    [Fact]
    public async Task A_push_in_is_refused_on_filmed_footage_rather_than_quietly_dropped()
    {
        var bench = Open();
        var shot = DesignNode.New(
            DesignNodeKind.Frame, null, ("text", "A shot"), ("src", @"C:\clips\walking.mp4"));

        bench.Session!.Record(bench.Session.Current.Add(shot), "Added a shot");

        var result = await Move(bench).InvokeAsync(
            new JsonObject { ["id"] = shot.Id, ["move"] = "grow" });

        Assert.False(result.Success);
        Assert.Contains("fade", result.FailureMessage, StringComparison.Ordinal);
        Assert.False(bench.Session.Current.Find(shot.Id)!.Props.ContainsKey("move"));
    }

    [Fact]
    public async Task A_fade_is_allowed_on_filmed_footage()
    {
        var bench = Open();
        var shot = DesignNode.New(
            DesignNodeKind.Frame, null, ("text", "A shot"), ("src", @"C:\clips\walking.mp4"));

        bench.Session!.Record(bench.Session.Current.Add(shot), "Added a shot");

        Assert.True((await Move(bench).InvokeAsync(
            new JsonObject { ["id"] = shot.Id, ["move"] = "fade" })).Success);
    }

    [Fact]
    public async Task Moving_something_that_is_not_there_says_so()
    {
        var result = await Move(Open()).InvokeAsync(
            new JsonObject { ["id"] = "nothing", ["move"] = "fade" });

        Assert.False(result.Success);
    }
}
