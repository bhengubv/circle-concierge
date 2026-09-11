using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What to make, rather than what colour to make it.
///
/// The six looks answer how a thing appears and say nothing about what goes on it, which is
/// the half somebody who is not a designer has no way to know — and the half a model gets
/// wrong by producing something competently laid out that says nothing.
///
/// open-design ships 298 of these. This ships a starter set and a file anybody can add to,
/// which is what `RoomCatalogue` does for furniture and for the same reason: what people want
/// is more of them, and more of them needs no code.
/// </summary>
public sealed class DesignGuideTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "concierge-guides", Guid.NewGuid().ToString("n"));

    private string Written(string json)
    {
        Directory.CreateDirectory(_folder);

        var path = Path.Combine(_folder, "guides.json");
        File.WriteAllText(path, json);

        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ── The guides themselves ─────────────────────────────────────────────

    [Fact]
    public void Some_ship_so_it_is_useful_before_anybody_writes_anything()
        => Assert.NotEmpty(new DesignGuides().All);

    /// <summary>
    /// A rule with no reason gets applied where it does not belong, and a rule nobody
    /// understands gets ignored the first time it is inconvenient. So every guide says why.
    /// </summary>
    [Fact]
    public void Every_one_is_prose_long_enough_to_carry_its_reasons()
    {
        foreach (var guide in DesignGuides.BuiltIn)
        {
            Assert.False(string.IsNullOrWhiteSpace(guide.When), guide.Name);
            Assert.True(guide.Guide.Length > 200, $"{guide.Name} is too short to say why.");
        }
    }

    [Fact]
    public void No_two_are_called_the_same_thing()
        => Assert.Equal(
            DesignGuides.BuiltIn.Count,
            DesignGuides.BuiltIn.Select(guide => guide.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    // ── Finding one ───────────────────────────────────────────────────────

    /// <summary>
    /// Nobody says "artifact type: presentation". They say "a deck for Thursday".
    /// </summary>
    [Theory]
    [InlineData("a poster for the school fair", "a poster")]
    [InlineData("I need a pitch deck for investors", "a pitch deck")]
    [InlineData("a dashboard of our numbers", "a dashboard")]
    [InlineData("design a phone screen for signing in", "a phone screen")]
    [InlineData("a room for the new office", "a room")]
    public void It_is_found_from_what_somebody_actually_said(string said, string expected)
        => Assert.Equal(expected, new DesignGuides().For(said)?.Name);

    [Fact]
    public void Its_own_name_finds_it_exactly()
        => Assert.Equal("a menu", new DesignGuides().For("a menu")?.Name);

    [Fact]
    public void Nothing_said_finds_nothing_rather_than_the_first_one()
    {
        Assert.Null(new DesignGuides().For(""));
        Assert.Null(new DesignGuides().For(null));
    }

    /// <summary>
    /// "the", "for" and "and" match everything and mean nothing, so short words are dropped
    /// rather than kept in a stop-word list somebody has to maintain.
    /// </summary>
    [Fact]
    public void Words_that_match_everything_match_nothing()
        => Assert.Null(new DesignGuides().For("the and for a to"));

    [Fact]
    public void Something_nobody_has_a_guide_for_comes_back_empty()
        => Assert.Null(new DesignGuides().For("a tessellation of hyperbolic manifolds"));

    // ── Adding your own ───────────────────────────────────────────────────

    [Fact]
    public void Anybody_can_add_one_without_writing_code()
    {
        var guides = new DesignGuides(Written("""
            [{ "name": "a risk register", "when": "Listing what could go wrong.",
               "guide": "One row a risk, worst first." }]
            """));

        Assert.Equal("One row a risk, worst first.", guides.For("a risk register")?.Guide);

        // And the built-ins are still there behind it.
        Assert.NotNull(guides.For("a poster"));
    }

    /// <summary>
    /// Somebody who writes a guide called "a poster" meant to replace the one that ships, not
    /// to sit behind it and never be found.
    /// </summary>
    [Fact]
    public void Yours_wins_over_one_of_ours_with_the_same_name()
    {
        var guides = new DesignGuides(Written("""
            [{ "name": "a poster", "when": "Ours.", "guide": "Put the badge top left, always." }]
            """));

        Assert.Equal("Put the badge top left, always.", guides.For("a poster")?.Guide);
        Assert.Single(guides.All, guide => guide.Name == "a poster");
    }

    /// <summary>
    /// A broken file costs the file, never the built-ins — the rule the hooks file and the
    /// shapes catalogue already follow.
    /// </summary>
    [Fact]
    public void A_broken_file_adds_nothing_and_says_why()
    {
        var guides = new DesignGuides(Written("{ not json at all"));

        Assert.NotEmpty(guides.All);
        Assert.NotNull(guides.Problem);
    }

    [Fact]
    public void A_file_that_is_not_there_is_simply_the_built_ins()
    {
        var guides = new DesignGuides(Path.Combine(_folder, "nothing-here.json"));

        Assert.Equal(DesignGuides.BuiltIn.Count, guides.All.Count);
        Assert.Null(guides.Problem);
    }

    [Fact]
    public void An_entry_with_no_advice_in_it_is_skipped_rather_than_listed_empty()
    {
        var guides = new DesignGuides(Written("""
            [{ "name": "a thing", "when": "Never." }]
            """));

        Assert.DoesNotContain(guides.All, guide => guide.Name == "a thing");
    }

    // ── As a tool ─────────────────────────────────────────────────────────

    private static IAgentTool Tool(DesignGuides? guides)
    {
        var bench = new DesignWorkbench { Guides = guides };
        bench.Attach(new DesignSession());

        return new DesignToolSource(bench).Tools.Single(tool => tool.Name == "design_guide");
    }

    [Fact]
    public async Task It_produces_advice_and_changes_nothing_so_it_does_not_ask()
    {
        var tool = Tool(new DesignGuides());

        Assert.True(tool.IsReadOnly);
        Assert.True((await tool.InvokeAsync(new JsonObject { ["about"] = "a poster" })).Success);
    }

    [Fact]
    public async Task Asked_about_something_it_hands_back_that_guide()
    {
        var result = await Tool(new DesignGuides())
            .InvokeAsync(new JsonObject { ["about"] = "a dashboard for the sales numbers" });

        Assert.Contains("a dashboard", result.Output, StringComparison.Ordinal);
        Assert.Contains("arrow", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Asked_nothing_it_says_what_there_is()
    {
        var result = await Tool(new DesignGuides()).InvokeAsync(new JsonObject());

        Assert.Contains("a poster", result.Output, StringComparison.Ordinal);
        Assert.Contains("a pitch deck", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model told only "no guide" has no idea whether it asked the wrong way or there is
    /// nothing for this at all — and it must not stop making the thing either way.
    /// </summary>
    [Fact]
    public async Task Asked_about_something_there_is_no_guide_for_it_says_so_and_lists_what_there_is()
    {
        var result = await Tool(new DesignGuides())
            .InvokeAsync(new JsonObject { ["about"] = "a tessellation of hyperbolic manifolds" });

        Assert.True(result.Success);
        Assert.Contains("no guide", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a poster", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A head with nowhere to keep a file still gets the advice. The guides are never absent.
    /// </summary>
    [Fact]
    public async Task With_no_file_anywhere_the_built_ins_are_still_there()
    {
        var result = await Tool(null).InvokeAsync(new JsonObject { ["about"] = "a poster" });

        Assert.True(result.Success);
        Assert.Contains("corridor", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
