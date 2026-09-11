using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// A room made of the things a room is made of.
///
/// Space drew four shapes on a floor, which is furniture without a building.
/// Pascal is an architectural editor and this is the part of it worth having: walls
/// that run between two points, floors and ceilings, a pitched roof, and doors and
/// windows that are actually holes.
///
/// **No CSG library was added for the holes, and that is the interesting bit.**
/// Cutting a shape out of a solid needs one; a door is a rectangle, so the hole is
/// left rather than cut — the wall is built as the stretches of solid either side
/// of the opening plus the piece over it. Exact for the shapes people ask for, and
/// free.
/// </summary>
public sealed class RoomBuildingTests
{
    private static DesignWorkbench Open()
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        return bench;
    }

    private static IAgentTool Tool(DesignWorkbench bench, string name)
        => new DesignToolSource(bench).Tools.Single(tool => tool.Name == name);

    /// <summary>The room as the engine is given it.</summary>
    private static JsonElement Numbers(DesignDocument document)
    {
        const string open = "<script type=\"application/json\" id=\"room\">";

        var html = DesignMediums.Render(document);
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);

        return JsonDocument.Parse(html[start..end]).RootElement;
    }

    private static JsonElement Only(DesignDocument document, string shape)
        => Numbers(document).GetProperty("things").EnumerateArray()
            .Single(thing => thing.GetProperty("shape").GetString() == shape);

    // ── Putting things up ─────────────────────────────────────────────────

    /// <summary>
    /// Somebody who says "put up a wall" on an empty canvas means a wall in a
    /// room, not a wall and then a puzzle about why nothing appeared.
    /// </summary>
    [Fact]
    public async Task Building_on_an_empty_canvas_makes_a_room_to_build_in()
    {
        var bench = Open();

        var result = await Tool(bench, "design_build")
            .InvokeAsync(new JsonObject { ["what"] = "wall" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal(DesignMedium.Scene, bench.Session!.Current.Medium);
        Assert.Single(DesignMediums.FramesOf(bench.Session.Current));
    }

    [Theory]
    [InlineData("wall")]
    [InlineData("floor")]
    [InlineData("ceiling")]
    [InlineData("roof")]
    public async Task Each_thing_a_room_is_made_of_can_be_put_up(string what)
    {
        var bench = Open();

        var result = await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = what });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal(what, Only(bench.Session!.Current, what).GetProperty("shape").GetString());
    }

    [Fact]
    public async Task Something_that_is_not_part_of_a_building_is_refused()
    {
        var result = await Tool(Open(), "design_build")
            .InvokeAsync(new JsonObject { ["what"] = "swimming pool" });

        Assert.False(result.Success);
    }

    /// <summary>
    /// A wall runs between two points because that is how somebody describes one —
    /// "a wall along the back" — and not as a width and a rotation, which is how a
    /// box is described and is why putting a wall up with `design_add` never
    /// worked.
    /// </summary>
    [Fact]
    public async Task A_wall_runs_between_two_points()
    {
        var bench = Open();

        await Tool(bench, "design_build").InvokeAsync(new JsonObject
        {
            ["what"] = "wall",
            ["x"] = -200,
            ["y"] = -150,
            ["x2"] = 200,
            ["y2"] = -150,
            ["height"] = 260,
        });

        var wall = Only(bench.Session!.Current, "wall");

        Assert.Equal(-200, wall.GetProperty("x").GetInt32());
        Assert.Equal(200, wall.GetProperty("x2").GetInt32());
        Assert.Equal(-150, wall.GetProperty("y2").GetInt32());
        Assert.Equal(260, wall.GetProperty("height").GetInt32());
    }

    /// <summary>
    /// A wall with no end would be drawn as nothing at all. It runs two metres
    /// along instead of being refused — a wall in roughly the right place can be
    /// moved, and a refusal leaves somebody with nothing to move.
    /// </summary>
    [Fact]
    public async Task A_wall_with_no_end_still_has_a_length()
    {
        var bench = Open();

        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall", ["x"] = 0 });

        var wall = Only(bench.Session!.Current, "wall");

        Assert.NotEqual(wall.GetProperty("x").GetInt32(), wall.GetProperty("x2").GetInt32());
    }

    // ── Doors and windows ─────────────────────────────────────────────────

    [Fact]
    public async Task A_door_is_cut_into_the_wall_it_belongs_to()
    {
        var bench = Open();

        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall" });
        var wall = Only(bench.Session!.Current, "wall");

        var result = await Tool(bench, "design_opening").InvokeAsync(new JsonObject
        {
            ["wall"] = wall.GetProperty("id").GetString(),
            ["what"] = "door",
            ["at"] = 40,
            ["width"] = 90,
        });

        Assert.True(result.Success, result.FailureMessage);

        var openings = Only(bench.Session.Current, "wall").GetProperty("openings");

        Assert.Equal(1, openings.GetArrayLength());
        Assert.Equal("door", openings[0].GetProperty("kind").GetString());
        Assert.Equal(40, openings[0].GetProperty("at").GetInt32());
    }

    /// <summary>
    /// A door starts at the floor and a window does not. Getting that wrong gives
    /// a window you can walk through.
    /// </summary>
    [Fact]
    public async Task A_window_sits_off_the_floor_and_a_door_does_not()
    {
        var bench = Open();
        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall" });

        var wallId = Only(bench.Session!.Current, "wall").GetProperty("id").GetString();
        var cut = Tool(bench, "design_opening");

        await cut.InvokeAsync(new JsonObject { ["wall"] = wallId, ["what"] = "door", ["at"] = 10 });
        await cut.InvokeAsync(new JsonObject { ["wall"] = wallId, ["what"] = "window", ["at"] = 150 });

        var openings = Only(bench.Session.Current, "wall").GetProperty("openings")
            .EnumerateArray()
            .ToDictionary(o => o.GetProperty("kind").GetString()!, o => o.GetProperty("sill").GetInt32());

        Assert.Equal(0, openings["door"]);
        Assert.True(openings["window"] > 0);
    }

    /// <summary>
    /// A door hung on a chair would be added happily and drawn nowhere, which
    /// looks exactly like nothing happening.
    /// </summary>
    [Fact]
    public async Task An_opening_cannot_be_cut_into_something_that_is_not_a_wall()
    {
        var bench = Open();

        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "floor" });
        var floor = Only(bench.Session!.Current, "floor");

        var result = await Tool(bench, "design_opening").InvokeAsync(new JsonObject
        {
            ["wall"] = floor.GetProperty("id").GetString(),
            ["what"] = "door",
        });

        Assert.False(result.Success);
        Assert.Contains("not a wall", result.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_opening_that_is_neither_a_door_nor_a_window_is_refused()
    {
        var bench = Open();
        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall" });

        var result = await Tool(bench, "design_opening").InvokeAsync(new JsonObject
        {
            ["wall"] = Only(bench.Session!.Current, "wall").GetProperty("id").GetString(),
            ["what"] = "hatch",
        });

        Assert.False(result.Success);
    }

    // ── What the engine is given ──────────────────────────────────────────

    /// <summary>
    /// The hole is left rather than cut, so the engine has to be told where it is.
    /// This is the contract the wall-building script reads.
    /// </summary>
    [Fact]
    public async Task The_engine_is_told_where_every_hole_goes()
    {
        var bench = Open();
        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall" });

        var wallId = Only(bench.Session!.Current, "wall").GetProperty("id").GetString();

        await Tool(bench, "design_opening").InvokeAsync(new JsonObject
        {
            ["wall"] = wallId, ["what"] = "window", ["at"] = 30, ["width"] = 60, ["height"] = 80, ["sill"] = 100,
        });

        var hole = Only(bench.Session.Current, "wall").GetProperty("openings")[0];

        foreach (var field in new[] { "kind", "at", "width", "height", "sill" })
        {
            Assert.True(hole.TryGetProperty(field, out _), $"the engine is not told {field}");
        }

        Assert.Equal(60, hole.GetProperty("width").GetInt32());
        Assert.Equal(100, hole.GetProperty("sill").GetInt32());
    }

    [Fact]
    public void The_engine_knows_how_to_draw_each_one()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "A room"))));

        foreach (var drawer in new[] { "wallOf", "slabOf", "roofOf" })
        {
            Assert.Contains($"function {drawer}(", html, StringComparison.Ordinal);
        }

        // The hole is left, not cut — so nothing is imported to do the cutting.
        // Asserted on the imports rather than the word, because the code says
        // "there is no CSG library here" in a comment and an earlier version of
        // this test caught that instead.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, @"import\("));
        Assert.Contains("three.module.js", html, StringComparison.Ordinal);
    }

    // ── Things that hang on something ─────────────────────────────────────

    /// <summary>
    /// Furniture floating in the middle of a room is the commonest thing to get
    /// wrong in a 3D surface, and it happens because somebody says "a shelf on the
    /// back wall" and something has to turn that into three numbers.
    /// </summary>
    [Fact]
    public async Task Something_can_be_put_against_a_wall()
    {
        var bench = Open();

        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall" });
        var wallId = Only(bench.Session!.Current, "wall").GetProperty("id").GetString();

        await Tool(bench, "design_add").InvokeAsync(new JsonObject
        {
            ["kind"] = "solid",
            ["text"] = "A bookcase",
        });

        var shelf = bench.Session.Current.Nodes.Values.Single(node => node.Text == "A bookcase");

        var result = await Tool(bench, "design_hang").InvokeAsync(new JsonObject
        {
            ["id"] = shelf.Id,
            ["on"] = wallId,
            ["at"] = 120,
            ["height"] = 90,
        });

        Assert.True(result.Success, result.FailureMessage);

        var after = bench.Session.Current.Find(shelf.Id)!;

        Assert.Equal(wallId, after.Props["on"]);
        Assert.Equal("120", after.Props["at"]);
        Assert.Equal("90", after.Props["sill"]);
    }

    [Fact]
    public async Task Something_can_hang_from_the_ceiling()
    {
        var bench = Open();
        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall" });

        await Tool(bench, "design_add").InvokeAsync(new JsonObject
        {
            ["kind"] = "solid",
            ["text"] = "A lamp",
        });

        var lamp = bench.Session!.Current.Nodes.Values.Single(node => node.Text == "A lamp");

        var result = await Tool(bench, "design_hang")
            .InvokeAsync(new JsonObject { ["id"] = lamp.Id, ["on"] = "ceiling" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal("ceiling", bench.Session.Current.Find(lamp.Id)!.Props["on"]);
    }

    /// <summary>
    /// Hanging a shelf on a chair would be accepted and then drawn wherever the
    /// shelf already was, which looks exactly like the tool doing nothing.
    /// </summary>
    [Fact]
    public async Task Something_cannot_hang_on_a_thing_that_is_not_a_wall()
    {
        var bench = Open();

        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "floor" });
        var floorId = Only(bench.Session!.Current, "floor").GetProperty("id").GetString();

        await Tool(bench, "design_add").InvokeAsync(new JsonObject { ["kind"] = "solid", ["text"] = "A shelf" });
        var shelf = bench.Session.Current.Nodes.Values.Single(node => node.Text == "A shelf");

        var result = await Tool(bench, "design_hang")
            .InvokeAsync(new JsonObject { ["id"] = shelf.Id, ["on"] = floorId });

        Assert.False(result.Success);
        Assert.Contains("not a wall", result.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Something_can_be_taken_off_the_wall_again()
    {
        var bench = Open();

        await Tool(bench, "design_build").InvokeAsync(new JsonObject { ["what"] = "wall" });
        var wallId = Only(bench.Session!.Current, "wall").GetProperty("id").GetString();

        await Tool(bench, "design_add").InvokeAsync(new JsonObject { ["kind"] = "solid", ["text"] = "A shelf" });
        var shelf = bench.Session.Current.Nodes.Values.Single(node => node.Text == "A shelf");

        var hang = Tool(bench, "design_hang");

        await hang.InvokeAsync(new JsonObject { ["id"] = shelf.Id, ["on"] = wallId });
        await hang.InvokeAsync(new JsonObject { ["id"] = shelf.Id, ["on"] = "floor" });

        Assert.Equal(string.Empty, bench.Session.Current.Find(shelf.Id)!.Props["on"]);
    }

    /// <summary>
    /// The engine has to be told what something hangs on, how far along, and how
    /// high — and it works the pushing-out distance from the wall's own thickness
    /// rather than being told, so a thing touches the wall instead of sitting
    /// inside it.
    /// </summary>
    [Fact]
    public void The_engine_is_told_what_hangs_on_what()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "A room"))));

        Assert.Contains("function hangOn(mesh, thing)", html, StringComparison.Ordinal);
        Assert.Contains("thing.on === 'ceiling'", html, StringComparison.Ordinal);
        Assert.Contains("wall.thickness", html, StringComparison.Ordinal);
    }

    // ── A plan to build on top of ─────────────────────────────────────────

    private const string Dot =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    /// <summary>
    /// How somebody with a drawing on paper starts: photograph it, say how wide
    /// the building really is, and put walls up over it. Not by typing
    /// coordinates.
    /// </summary>
    [Fact]
    public async Task A_plan_can_be_laid_on_the_floor_at_its_real_size()
    {
        var bench = Open();

        var result = await Tool(bench, "design_plan").InvokeAsync(new JsonObject
        {
            ["picture"] = Dot,
            ["width"] = 900,
            ["depth"] = 600,
        });

        Assert.True(result.Success, result.FailureMessage);

        var plan = Only(bench.Session!.Current, "plan");

        Assert.Equal(900, plan.GetProperty("width").GetInt32());
        Assert.Equal(600, plan.GetProperty("depth").GetInt32());
        Assert.StartsWith("data:image/", plan.GetProperty("src").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule the paperclip follows everywhere else. A plan living on somebody's
    /// machine makes a design that looks complete here and arrives somewhere else
    /// with nothing to trace.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/plan.png")]
    [InlineData("C:/plans/ground-floor.png")]
    [InlineData("")]
    public async Task A_plan_that_does_not_travel_with_the_design_is_refused(string picture)
    {
        var result = await Tool(Open(), "design_plan")
            .InvokeAsync(new JsonObject { ["picture"] = picture });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Laying_a_plan_on_an_empty_canvas_makes_a_room_for_it()
    {
        var bench = Open();

        await Tool(bench, "design_plan").InvokeAsync(new JsonObject { ["picture"] = Dot });

        Assert.Equal(DesignMedium.Scene, bench.Session!.Current.Medium);
        Assert.Single(DesignMediums.FramesOf(bench.Session.Current));
    }

    /// <summary>
    /// Laid under everything by a hair, so a wall drawn on the line covers it
    /// rather than fighting it for the same pixels.
    /// </summary>
    [Fact]
    public void The_engine_lays_the_plan_under_what_is_built_on_it()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "A room"))));

        Assert.Contains("function guideOf(thing)", html, StringComparison.Ordinal);
        Assert.Contains("renderOrder = -1", html, StringComparison.Ordinal);
        Assert.Contains("depthWrite: false", html, StringComparison.Ordinal);
    }
}
