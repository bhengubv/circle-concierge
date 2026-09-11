using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// A phone screen and a board of numbers, as their own media.
///
/// open-design ships mobile-app designs and live dashboards among its six artifact
/// types. Both are here now, and both are their own medium rather than a page in a
/// narrow window — because the difference that matters is not the width.
///
/// A phone has a strip at the top nothing may go under, a strip at the bottom the
/// same, and a thumb that reaches about two thirds of the way up. A board is read
/// in two seconds from across a room. Neither of those is a page.
/// </summary>
public sealed class PhoneAndBoardTests
{
    private static DesignDocument With(DesignMedium medium, params DesignNode[] frames)
    {
        var document = DesignDocument.Blank(medium: medium);

        foreach (var frame in frames)
        {
            document = document.Add(frame);
        }

        return document;
    }

    private static DesignNode Frame(string text, params (string Key, string Value)[] props)
        => DesignNode.New(DesignNodeKind.Frame, null, [("text", text), .. props]);

    private static DesignWorkbench Open(DesignMedium medium)
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        bench.Session!.Record(bench.Session.Current.As(medium), "Made it");
        return bench;
    }

    private static IAgentTool Tool(DesignWorkbench bench, string name)
        => new DesignToolSource(bench).Tools.Single(tool => tool.Name == name);

    // ── A phone screen ────────────────────────────────────────────────────

    /// <summary>
    /// Drawn rather than described, so anything pushed under the notch is visible
    /// as wrong while it is being made rather than after it is built.
    /// </summary>
    [Fact]
    public void A_screen_draws_the_strips_nothing_may_go_under()
    {
        var html = DesignMediums.Render(With(DesignMedium.Handheld, Frame("Sign in")));

        Assert.Contains("class=\"safe top\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"safe bottom\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Put the button where somebody can press it" is a rule everybody agrees
    /// with and nobody applies. Drawing the line is applying it.
    /// </summary>
    [Fact]
    public void A_screen_draws_the_line_a_thumb_reaches()
    {
        var html = DesignMediums.Render(With(DesignMedium.Handheld, Frame("Sign in")));

        Assert.Contains("class=\"reach\"", html, StringComparison.Ordinal);
        Assert.Contains("within reach", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A drawn phone makes a mock-up look finished and tells nobody anything. The
    /// two lines that matter are drawn; the battery icon is not.
    /// </summary>
    [Fact]
    public void A_screen_is_a_screen_and_not_a_picture_of_a_phone()
    {
        var html = DesignMediums.Render(With(DesignMedium.Handheld, Frame("Sign in")));

        // Asserted on what is drawn rather than on the words, because the code
        // says "no fake battery" in a comment and an earlier version of this test
        // caught that instead — the same trap as the CSG one.
        Assert.DoesNotContain("class=\"device", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"bezel", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"statusbar", html, StringComparison.Ordinal);
    }

    [Fact]
    public void What_is_on_a_screen_is_drawn_on_it()
    {
        var screen = Frame("Sign in");

        var html = DesignMediums.Render(
            With(DesignMedium.Handheld, screen)
                .Add(DesignNode.New(DesignNodeKind.Heading, screen.Id, ("text", "Welcome back"))));

        Assert.Contains("Welcome back", html, StringComparison.Ordinal);
        Assert.Contains("Sign in", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phone_with_no_screens_says_what_to_say()
        => Assert.Contains(
            "add a screen",
            DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Handheld)),
            StringComparison.OrdinalIgnoreCase);

    // ── A board ───────────────────────────────────────────────────────────

    [Fact]
    public void A_panel_shows_its_number_large_and_its_name_small()
    {
        var html = DesignMediums.Render(
            With(DesignMedium.Board, Frame("Revenue", ("value", "48,200"))));

        Assert.Contains("48,200", html, StringComparison.Ordinal);
        Assert.Contains("class=\"value\"", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Revenue</h2>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// open-design's own rule, adopted unchanged: an invented metric is slop the
    /// moment it is invented. The fix is a labelled blank, never a plausible
    /// number.
    /// </summary>
    [Fact]
    public void A_panel_with_no_number_shows_a_blank_rather_than_inventing_one()
    {
        var html = DesignMediums.Render(With(DesignMedium.Board, Frame("Revenue")));

        Assert.Contains("class=\"value blank\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leading minus is down and everything else is up: right far more often
    /// than it is wrong, and nobody has to learn a convention.
    /// </summary>
    [Theory]
    [InlineData("+12%", "up")]
    [InlineData("-3", "down")]
    [InlineData("down 4", "down")]
    [InlineData("steady", "steady")]
    public void Which_way_a_number_is_moving_is_read_from_how_it_was_written(string change, string way)
    {
        var html = DesignMediums.Render(
            With(DesignMedium.Board, Frame("Revenue", ("value", "100"), ("change", change))));

        Assert.Contains($"class=\"change {way}\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Colour alone says nothing to somebody who cannot tell red from green, and a
    /// board is read at a glance by whoever is walking past. The arrow carries the
    /// meaning; the colour only helps.
    /// </summary>
    [Fact]
    public void Which_way_it_is_moving_is_not_carried_by_colour_alone()
    {
        var up = DesignMediums.Render(
            With(DesignMedium.Board, Frame("A", ("value", "1"), ("change", "+5"))));

        var down = DesignMediums.Render(
            With(DesignMedium.Board, Frame("A", ("value", "1"), ("change", "-5"))));

        Assert.Contains("▲", up, StringComparison.Ordinal);
        Assert.Contains("▼", down, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line going up is the thing every dashboard reaches for and almost nobody
    /// reads. From across a room it is a shape; up close it needs axes, a scale
    /// and a legend before it says anything.
    /// </summary>
    [Fact]
    public void A_board_draws_no_chart()
    {
        var html = DesignMediums.Render(
            With(DesignMedium.Board, Frame("Revenue", ("value", "100"), ("change", "+5"))));

        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<canvas", html, StringComparison.OrdinalIgnoreCase);
    }

    // ── Setting a panel ───────────────────────────────────────────────────

    [Fact]
    public async Task A_panel_can_be_told_what_to_say()
    {
        var bench = Open(DesignMedium.Board);
        var panel = Frame("Revenue");
        bench.Session!.Record(bench.Session.Current.Add(panel), "Added a panel");

        var result = await Tool(bench, "design_panel").InvokeAsync(new JsonObject
        {
            ["id"] = panel.Id,
            ["value"] = "48,200",
            ["change"] = "+12%",
            ["note"] = "Since Monday",
        });

        Assert.True(result.Success, result.FailureMessage);

        var after = bench.Session.Current.Find(panel.Id)!;

        Assert.Equal("48,200", after.Props["value"]);
        Assert.Equal("+12%", after.Props["change"]);
        Assert.Equal("Since Monday", after.Props["note"]);
    }

    /// <summary>
    /// Only what was actually said. Writing an empty string for a field nobody
    /// mentioned would wipe the note every time somebody updated the number.
    /// </summary>
    [Fact]
    public async Task Updating_the_number_leaves_the_note_alone()
    {
        var bench = Open(DesignMedium.Board);
        var panel = Frame("Revenue", ("note", "Since Monday"));
        bench.Session!.Record(bench.Session.Current.Add(panel), "Added a panel");

        await Tool(bench, "design_panel")
            .InvokeAsync(new JsonObject { ["id"] = panel.Id, ["value"] = "50,000" });

        var after = bench.Session.Current.Find(panel.Id)!;

        Assert.Equal("50,000", after.Props["value"]);
        Assert.Equal("Since Monday", after.Props["note"]);
    }

    [Fact]
    public async Task Setting_a_panel_that_is_not_there_says_so()
    {
        var result = await Tool(Open(DesignMedium.Board), "design_panel")
            .InvokeAsync(new JsonObject { ["id"] = "nothing", ["value"] = "1" });

        Assert.False(result.Success);
    }

    // ── Both, as media ────────────────────────────────────────────────────

    [Fact]
    public void Both_can_be_asked_for_by_name()
    {
        Assert.Contains(DesignMediums.All, m => m.Name == "Phone");
        Assert.Contains(DesignMediums.All, m => m.Name == "Board");
        Assert.Equal("screen", DesignMediums.PieceOf(DesignMedium.Handheld));
        Assert.Equal("panel", DesignMediums.PieceOf(DesignMedium.Board));
    }

    /// <summary>
    /// Changing what you are making throws nothing away — the rule every medium
    /// here follows.
    /// </summary>
    [Fact]
    public void Turning_a_page_into_a_screen_keeps_what_was_on_it()
    {
        var page = DesignDocument.Blank(medium: DesignMedium.Page)
            .Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Welcome back")));

        Assert.Contains("Welcome back", DesignMediums.Render(page.As(DesignMedium.Handheld)),
            StringComparison.Ordinal);

        Assert.Contains("Welcome back", DesignMediums.Render(page.As(DesignMedium.Board)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("add a screen")]
    [InlineData("add a panel")]
    public void Both_can_be_added_by_saying_so(string said)
    {
        var heard = DesignSpeech.Hear(DesignDocument.Blank(medium: DesignMedium.Handheld), said, null);

        Assert.True(heard.Understood, said);
        Assert.Contains(heard.Document.Nodes.Values, node => node.Kind == DesignNodeKind.Frame);
    }
}
