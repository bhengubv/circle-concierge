using System.Text.Json.Nodes;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// The canvas, on the same agent loop as everything else.
///
/// Design was a separate path. `SendAsync` returned early whenever the canvas was
/// open, so a sentence reached `DesignSpeech` and never a model — no tools, no
/// plan, no skills. The comment above it said "a model still handles everything
/// this cannot", and nothing did: there was no fall-through and there never had
/// been. Fifth comment in this repository describing behaviour that did not exist.
///
/// This is the seam that makes the canvas part of the harness. The tests that
/// matter are the two edges: nothing offered when no canvas is open, and an honest
/// failure rather than a silent success when it closes underneath a call.
/// </summary>
public sealed class DesignToolSourceTests
{
    private static (DesignToolSource Tools, DesignWorkbench Bench, DesignSession Session) Open()
    {
        var bench = new DesignWorkbench();
        var session = new DesignSession();
        bench.Attach(session);
        return (new DesignToolSource(bench), bench, session);
    }

    private static Task<Concierge.Shared.Tools.AgentToolResult> Call(
        DesignToolSource tools, string name, JsonObject? args = null)
        => tools.Tools.Single(t => t.Name == name).InvokeAsync(args);

    /// <summary>
    /// A model offered `design_add` while looking at a chat would use it, and
    /// report a heading added to something nobody can see.
    /// </summary>
    [Fact]
    public void With_no_canvas_open_nothing_is_offered()
        => Assert.Empty(new DesignToolSource(new DesignWorkbench()).Tools);

    [Fact]
    public void With_a_canvas_open_the_design_tools_appear()
    {
        var (tools, _, _) = Open();

        Assert.Contains(tools.Tools, t => t.Name == "design_describe");
        Assert.Contains(tools.Tools, t => t.Name == "design_add");
        Assert.Contains(tools.Tools, t => t.Name == "design_change");
    }

    [Fact]
    public void Closing_the_canvas_takes_the_tools_away_again()
    {
        var (tools, bench, _) = Open();
        Assert.NotEmpty(tools.Tools);

        bench.Detach();

        Assert.Empty(tools.Tools);
    }

    /// <summary>
    /// Only describing is read-only. Everything else changes the canvas, and a
    /// tool that lied about that would be run unattended.
    /// </summary>
    [Fact]
    public void Only_describing_is_read_only()
    {
        var (tools, _, _) = Open();

        Assert.True(tools.Tools.Single(t => t.Name == "design_describe").IsReadOnly);
        Assert.All(
            tools.Tools.Where(t => t.Name != "design_describe"),
            tool => Assert.False(tool.IsReadOnly));
    }

    [Fact]
    public async Task Describing_an_empty_canvas_says_it_is_empty()
    {
        var (tools, _, _) = Open();

        var result = await Call(tools, "design_describe");

        Assert.True(result.Success);
        Assert.Contains("empty", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adding_something_puts_it_on_the_canvas_and_returns_its_id()
    {
        var (tools, _, session) = Open();

        var result = await Call(tools, "design_add",
            new JsonObject { ["kind"] = "heading", ["text"] = "Hello" });

        Assert.True(result.Success);
        Assert.False(session.Current.IsEmpty);
        Assert.Contains("Hello", (await Call(tools, "design_describe")).Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Something_that_cannot_be_added_is_refused_with_the_list_of_what_can()
    {
        var (tools, _, _) = Open();

        var result = await Call(tools, "design_add", new JsonObject { ["kind"] = "hologram" });

        Assert.False(result.Success);
        Assert.Contains("heading", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A page is the root and there is exactly one. Adding a second would give the
    /// document two roots and no way to draw it.
    /// </summary>
    [Fact]
    public async Task A_second_page_cannot_be_added()
    {
        var (tools, _, _) = Open();

        Assert.False((await Call(tools, "design_add", new JsonObject { ["kind"] = "page" })).Success);
    }

    [Fact]
    public async Task Changing_something_that_is_not_there_says_so_rather_than_doing_nothing()
    {
        var (tools, _, _) = Open();

        var result = await Call(tools, "design_change", new JsonObject
        {
            ["id"] = "nothing", ["property"] = "text", ["value"] = "x",
        });

        Assert.False(result.Success);
        Assert.Contains("nothing", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Changing_something_that_is_there_changes_it()
    {
        var (tools, _, session) = Open();
        await Call(tools, "design_add", new JsonObject { ["kind"] = "heading", ["text"] = "Before" });

        var id = session.Current.ChildrenOf(session.Current.RootId).Single().Id;

        var result = await Call(tools, "design_change", new JsonObject
        {
            ["id"] = id, ["property"] = "text", ["value"] = "After",
        });

        Assert.True(result.Success);
        Assert.Equal("After", session.Current.Find(id)!.Text);
    }

    [Fact]
    public async Task Removing_something_takes_it_off()
    {
        var (tools, _, session) = Open();
        await Call(tools, "design_add", new JsonObject { ["kind"] = "heading", ["text"] = "Gone soon" });
        var id = session.Current.ChildrenOf(session.Current.RootId).Single().Id;

        Assert.True((await Call(tools, "design_remove", new JsonObject { ["id"] = id })).Success);
        Assert.Null(session.Current.Find(id));
    }

    /// <summary>
    /// An unknown look lands on the default rather than failing, which is what the
    /// picker does when nobody has chosen. A wrong look is one more sentence to
    /// fix; an error is a dead end.
    /// </summary>
    [Fact]
    public async Task An_unknown_look_falls_back_rather_than_failing()
    {
        var (tools, _, session) = Open();

        var result = await Call(tools, "design_look", new JsonObject { ["look"] = "Ultraviolet" });

        Assert.True(result.Success);
        Assert.Equal(DesignLooks.Default, session.Current.Look);
    }

    [Fact]
    public async Task Changing_what_is_being_made_keeps_what_is_on_it()
    {
        var (tools, _, session) = Open();
        await Call(tools, "design_add", new JsonObject { ["kind"] = "heading", ["text"] = "Kept" });

        var result = await Call(tools, "design_making", new JsonObject { ["making"] = "deck" });

        Assert.True(result.Success);
        Assert.Equal(DesignMedium.Deck, session.Current.Medium);
        Assert.Contains("Kept", (await Call(tools, "design_describe")).Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Going_back_undoes_the_last_change()
    {
        var (tools, _, session) = Open();
        await Call(tools, "design_add", new JsonObject { ["kind"] = "heading", ["text"] = "Undo me" });
        Assert.False(session.Current.IsEmpty);

        Assert.True((await Call(tools, "design_go_back")).Success);
        Assert.True(session.Current.IsEmpty);
    }

    [Fact]
    public async Task Going_back_at_the_beginning_says_so_rather_than_failing()
    {
        var (tools, _, _) = Open();

        var result = await Call(tools, "design_go_back");

        Assert.True(result.Success);
        Assert.Contains("as far back", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The canvas can close between the catalogue being read and the tool being
    /// called. A model told nothing happened can say so; one told nothing at all
    /// reports that it did the thing.
    /// </summary>
    [Fact]
    public async Task A_canvas_that_closes_underneath_a_call_fails_honestly()
    {
        var (tools, bench, _) = Open();
        var add = tools.Tools.Single(t => t.Name == "design_add");

        bench.Detach();

        var result = await add.InvokeAsync(new JsonObject { ["kind"] = "heading" });

        Assert.False(result.Success);
        Assert.Contains("no design canvas", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── The look, as an argument rather than a name ───────────────────────

    /// <summary>
    /// A model told only "Warm" adds a hard-edged banner and four accent colours,
    /// and produces something wearing the name and none of the intent. The brief is
    /// the argument behind the look, and it is only worth writing if something
    /// reads it — six paragraphs nothing opens is the trap this repository keeps
    /// falling into.
    /// </summary>
    [Fact]
    public async Task Describing_the_canvas_says_what_the_look_is_for()
    {
        var (tools, _, _) = Open();

        var described = (await Call(tools, "design_describe")).Output;

        Assert.Contains("What that look is for", described, StringComparison.Ordinal);
        Assert.Contains(DesignLooks.Of(DesignLooks.Default).Brief, described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_look_hands_back_what_that_look_is_for()
    {
        var (tools, _, _) = Open();

        var result = await Call(tools, "design_look", new JsonObject { ["look"] = "Warm" });

        Assert.True(result.Success);
        Assert.Contains(DesignLooks.Of("Warm").Brief, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_look_carries_an_argument_and_not_just_a_sentence()
    {
        Assert.All(DesignLooks.All, look =>
        {
            Assert.False(string.IsNullOrWhiteSpace(look.Brief));

            // Long enough to be a reason rather than a restatement of the blurb.
            Assert.True(look.Brief.Length > 200, $"{look.Name} has no real brief.");
            Assert.NotEqual(look.Blurb, look.Brief);
        });
    }
}
