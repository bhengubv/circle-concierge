using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// The things a room can be furnished with, and how somebody adds their own.
///
/// **Pascal's plugin idea with the dangerous half left out.** Pascal lets people
/// write add-ons that contribute node types, renderers and panels — real code,
/// loaded into the editor. `PluginHost` here does the equivalent and is
/// deliberately not wired up, because "any DLL on disk can add tools" is a
/// decision somebody makes on purpose rather than by finishing a wiring job. That
/// has not changed and this does not change it.
///
/// What people actually want from those add-ons is nearly always more things to
/// put in the room, and that needs no code. A file naming a desk as three boxes is
/// an add-on anybody can write, cannot execute anything, and cannot break the app.
/// </summary>
public sealed class RoomCatalogueTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-catalogue-tests", Guid.NewGuid().ToString("N"));

    public RoomCatalogueTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    private string Out(string name) => Path.Combine(_root, name);

    private RoomCatalogue Catalogue(string? written = null)
    {
        var path = Out("shapes.json");

        if (written is not null)
        {
            File.WriteAllText(path, written);
        }

        return new RoomCatalogue(path);
    }

    private static DesignWorkbench Open(RoomCatalogue catalogue)
    {
        var bench = new DesignWorkbench { Catalogue = catalogue };
        bench.Attach(new DesignSession());
        return bench;
    }

    private static IAgentTool Tool(DesignWorkbench bench, string name)
        => new DesignToolSource(bench).Tools.Single(tool => tool.Name == name);

    // ── What ships ────────────────────────────────────────────────────────

    /// <summary>
    /// A feature that only works once you have configured it is a feature most
    /// people never see.
    /// </summary>
    [Fact]
    public void There_are_things_to_put_in_a_room_before_anybody_writes_a_file()
    {
        var names = Catalogue().Things.Select(thing => thing.Name).ToList();

        Assert.Contains("desk", names);
        Assert.Contains("chair", names);
        Assert.Contains("bed", names);
    }

    /// <summary>
    /// A room built at the size of real furniture is a room somebody can judge. A
    /// desk that is a nice number of units and the wrong size is worse than no
    /// desk — so a desk is about seventy centimetres tall, like a desk.
    /// </summary>
    [Fact]
    public void Things_are_the_size_that_things_actually_are()
    {
        var desk = Catalogue().Find("desk")!;
        var top = desk.Parts.MaxBy(part => part.Sill)!;

        Assert.InRange(top.Sill, 65, 80);
        Assert.InRange(desk.Parts.Max(part => part.Width), 100, 200);
    }

    [Fact]
    public void A_name_is_found_however_it_is_written()
    {
        Assert.NotNull(Catalogue().Find("DESK"));
        Assert.NotNull(Catalogue().Find("  chair  "));
        Assert.Null(Catalogue().Find("hovercraft"));
    }

    // ── What somebody adds ────────────────────────────────────────────────

    [Fact]
    public void Somebody_can_add_a_thing_of_their_own()
    {
        var catalogue = Catalogue("""
            [
              { "name": "workbench",
                "parts": [ { "shape": "box", "width": 180, "depth": 80, "height": 8, "sill": 90 } ] }
            ]
            """);

        var made = catalogue.Find("workbench");

        Assert.NotNull(made);
        Assert.Equal(180, made!.Parts[0].Width);
    }

    /// <summary>
    /// Somebody's own definition wins over a built-in of the same name. That is
    /// the whole point of being able to write one.
    /// </summary>
    [Fact]
    public void Their_version_wins_over_the_one_that_ships()
    {
        var catalogue = Catalogue("""
            [ { "name": "desk", "parts": [ { "shape": "box", "width": 999, "depth": 10, "height": 10 } ] } ]
            """);

        Assert.Equal(999, catalogue.Find("desk")!.Parts[0].Width);
        Assert.Single(catalogue.Things.Where(thing => thing.Name == "desk"));
    }

    /// <summary>
    /// One bad entry must not cost the rest. A nameless thing cannot be asked for
    /// and a thing with no parts draws nothing, so both are skipped.
    /// </summary>
    [Fact]
    public void One_bad_entry_does_not_take_the_others_with_it()
    {
        var catalogue = Catalogue("""
            [
              { "name": "", "parts": [ { "shape": "box" } ] },
              { "name": "nothing", "parts": [] },
              { "name": "stool", "parts": [ { "shape": "cylinder", "width": 35, "depth": 35, "height": 45 } ] }
            ]
            """);

        Assert.NotNull(catalogue.Find("stool"));
        Assert.Null(catalogue.Find("nothing"));
    }

    /// <summary>
    /// A memory aid must never break the thing it is helping with — the same rule
    /// the hooks file and the design log follow.
    /// </summary>
    [Fact]
    public void A_file_that_cannot_be_read_leaves_the_built_ins_working()
    {
        var catalogue = Catalogue("{ this is not the catalogue }");

        Assert.NotNull(catalogue.Find("desk"));
        Assert.NotNull(catalogue.Problem);
    }

    [Fact]
    public void A_changed_file_is_picked_up_when_asked()
    {
        var catalogue = Catalogue("""
            [ { "name": "crate", "parts": [ { "shape": "box", "width": 60, "depth": 60, "height": 60 } ] } ]
            """);

        Assert.NotNull(catalogue.Find("crate"));

        File.WriteAllText(Out("shapes.json"), """
            [ { "name": "barrel", "parts": [ { "shape": "cylinder", "width": 50, "depth": 50, "height": 80 } ] } ]
            """);

        catalogue.Again();

        Assert.NotNull(catalogue.Find("barrel"));
        Assert.Null(catalogue.Find("crate"));
    }

    // ── Putting one in ────────────────────────────────────────────────────

    [Fact]
    public async Task A_named_thing_goes_into_the_room_in_pieces()
    {
        var bench = Open(Catalogue());

        var result = await Tool(bench, "design_furnish")
            .InvokeAsync(new JsonObject { ["what"] = "desk", ["x"] = 40, ["y"] = -20 });

        Assert.True(result.Success, result.FailureMessage);

        var parts = bench.Session!.Current.Nodes.Values
            .Where(node => node.Kind == DesignNodeKind.Solid && node.Text == "desk")
            .ToList();

        Assert.Equal(3, parts.Count);

        // Placed where it was asked for, with each piece offset from there.
        Assert.Contains(parts, part => part.Props["x"] == "40");
    }

    /// <summary>
    /// A model that guessed once will guess again, so the refusal carries the list.
    /// </summary>
    [Fact]
    public async Task Asking_for_something_that_is_not_there_says_what_is()
    {
        var result = await Tool(Open(Catalogue()), "design_furnish")
            .InvokeAsync(new JsonObject { ["what"] = "hovercraft" });

        Assert.False(result.Success);
        Assert.Contains("desk", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_what_there_is_answers_rather_than_failing()
    {
        var result = await Tool(Open(Catalogue()), "design_furnish").InvokeAsync(new JsonObject());

        Assert.True(result.Success);
        Assert.Contains("sofa", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Furnishing_an_empty_canvas_makes_a_room_to_furnish()
    {
        var bench = Open(Catalogue());

        await Tool(bench, "design_furnish").InvokeAsync(new JsonObject { ["what"] = "chair" });

        Assert.Equal(DesignMedium.Scene, bench.Session!.Current.Medium);
        Assert.Single(DesignMediums.FramesOf(bench.Session.Current));
    }
}
