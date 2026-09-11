using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// A building with more than one floor, and the three ways of looking at it.
///
/// Whole, pulled apart, or one floor at a time. **Pulled apart is the one worth
/// having**: a house from outside is a box, and a house with its floors lifted
/// away from each other is a thing you can read — the one view no physical model
/// gives you.
///
/// Taken from Pascal, which calls the three stacked, exploded and solo. The words
/// here are the ones somebody would say out loud, which is the only difference
/// that matters on this surface.
/// </summary>
public sealed class BuildingFloorTests
{
    private static DesignWorkbench Open()
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        return bench;
    }

    private static IAgentTool Tool(DesignWorkbench bench, string name)
        => new DesignToolSource(bench).Tools.Single(tool => tool.Name == name);

    private static JsonElement Numbers(DesignDocument document)
    {
        const string open = "<script type=\"application/json\" id=\"room\">";

        var html = DesignMediums.Render(document);
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);

        return JsonDocument.Parse(html[start..end]).RootElement;
    }

    private static async Task<DesignWorkbench> TwoFloorsAsync()
    {
        var bench = Open();
        var build = Tool(bench, "design_build");

        await build.InvokeAsync(new JsonObject { ["what"] = "wall", ["text"] = "downstairs wall" });
        await build.InvokeAsync(new JsonObject { ["what"] = "wall", ["text"] = "upstairs wall" });

        var walls = bench.Session!.Current.Nodes.Values
            .Where(node => node.Props.TryGetValue("shape", out var shape) && shape == "wall")
            .ToList();

        await Tool(bench, "design_floor_of").InvokeAsync(new JsonObject
        {
            ["id"] = walls[1].Id,
            ["floor"] = 1,
        });

        return bench;
    }

    // ── Which floor something is on ───────────────────────────────────────

    /// <summary>
    /// Floors are decided after the fact far more often than before: somebody puts
    /// up a room, likes it, and then says the whole thing is upstairs.
    /// </summary>
    [Fact]
    public async Task Something_can_be_moved_to_another_floor_after_it_is_built()
    {
        var bench = await TwoFloorsAsync();

        var floors = Numbers(bench.Session!.Current).GetProperty("things").EnumerateArray()
            .Select(thing => thing.GetProperty("level").GetInt32())
            .ToList();

        Assert.Contains(0, floors);
        Assert.Contains(1, floors);
    }

    [Fact]
    public async Task Moving_something_that_is_not_there_says_so()
    {
        var result = await Tool(Open(), "design_floor_of")
            .InvokeAsync(new JsonObject { ["id"] = "nothing", ["floor"] = 1 });

        Assert.False(result.Success);
    }

    /// <summary>
    /// There is no floor below the ground. A negative one would lift everything
    /// the wrong way and read as a bug rather than a basement.
    /// </summary>
    [Fact]
    public async Task A_floor_below_the_ground_becomes_the_ground_floor()
    {
        var bench = Open();
        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall" });

        var wall = bench.Session!.Current.Nodes.Values
            .Single(node => node.Props.TryGetValue("shape", out var shape) && shape == "wall");

        await Tool(bench, "design_floor_of").InvokeAsync(new JsonObject
        {
            ["id"] = wall.Id,
            ["floor"] = -4,
        });

        Assert.Equal("0", bench.Session.Current.Find(wall.Id)!.Props["level"]);
    }

    // ── The three ways of looking ─────────────────────────────────────────

    [Theory]
    [InlineData("whole", "whole")]
    [InlineData("all", "whole")]
    [InlineData("apart", "apart")]
    [InlineData("exploded", "apart")]
    [InlineData("one", "one")]
    public async Task A_building_can_be_looked_at_three_ways(string said, string expected)
    {
        var bench = await TwoFloorsAsync();

        var result = await Tool(bench, "design_floors").InvokeAsync(new JsonObject { ["how"] = said });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal(expected, Numbers(bench.Session!.Current).GetProperty("showing").GetString());
    }

    [Fact]
    public async Task A_way_of_looking_that_does_not_exist_is_refused()
    {
        var bench = await TwoFloorsAsync();

        var result = await Tool(bench, "design_floors")
            .InvokeAsync(new JsonObject { ["how"] = "sideways" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Looking_at_one_floor_says_which_one()
    {
        var bench = await TwoFloorsAsync();

        await Tool(bench, "design_floors")
            .InvokeAsync(new JsonObject { ["how"] = "one", ["which"] = 1 });

        var room = Numbers(bench.Session!.Current);

        Assert.Equal("one", room.GetProperty("showing").GetString());
        Assert.Equal(1, room.GetProperty("only").GetInt32());
    }

    /// <summary>
    /// With nothing built there is nothing to look at, and saying so beats setting
    /// a view on a building that does not exist.
    /// </summary>
    [Fact]
    public async Task Looking_at_a_building_that_is_not_there_says_so()
    {
        var result = await Tool(Open(), "design_floors").InvokeAsync(new JsonObject { ["how"] = "apart" });

        Assert.False(result.Success);
    }

    /// <summary>
    /// Kept on the room rather than in the component, so going back to an earlier
    /// picture brings back how you were looking at it as well as what you were
    /// looking at.
    /// </summary>
    [Fact]
    public async Task How_you_were_looking_at_it_comes_back_with_the_picture()
    {
        var bench = await TwoFloorsAsync();

        await Tool(bench, "design_floors").InvokeAsync(new JsonObject { ["how"] = "apart" });
        Assert.Equal("apart", Numbers(bench.Session!.Current).GetProperty("showing").GetString());

        bench.Session.Back();

        Assert.NotEqual("apart", Numbers(bench.Session.Current).GetProperty("showing").GetString());
    }

    // ── What the engine is told ───────────────────────────────────────────

    [Fact]
    public void The_engine_knows_how_to_stack_lift_and_hide_floors()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "A room"))));

        Assert.Contains("function riseOf(level)", html, StringComparison.Ordinal);
        Assert.Contains("function shown(level)", html, StringComparison.Ordinal);

        // A floor whose furniture stayed on the ground would be worse than no
        // floors at all, so everything is lifted in one place.
        Assert.Contains("mesh.position.y += riseOf(thing.level)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_building_with_one_floor_is_shown_whole_by_default()
    {
        var room = Numbers(DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "A room"))));

        Assert.Equal("whole", room.GetProperty("showing").GetString());
        Assert.Equal(0, room.GetProperty("only").GetInt32());
    }
}
