using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// The order the room's script does things in.
///
/// **This exists because the 3D engine was broken for anybody who put anything in a room, and
/// every test passed.** `riseOf` reads `storeys`, the drawing loop calls `riseOf` for every
/// thing, and `var storeys` was assigned *after* the loop — hoisted, so it was `undefined`
/// when the first solid asked for it. "Cannot read properties of undefined (reading
/// 'indexOf')", caught, and the whole engine fell back to the flat room.
///
/// **An empty room never entered the loop**, so it never threw, so the verification that
/// recorded `drew: true` was done on a room with nothing in it. The engine did draw. There
/// was nothing to draw. Anything put in a room after that was flat, and the only thing that
/// said so was Engineering's own report — which is why that report earns its place.
///
/// Testing generated JavaScript by reading it is crude, and it is what is available: no test
/// here runs a browser. What it pins is exactly the thing that went wrong — an order, in one
/// file, that nothing else can check.
/// </summary>
public sealed class SceneEngineOrderTests
{
    private static string RoomWithThingsInIt()
    {
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "The office"));

        var document = DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(room)
            .Add(DesignNode.New(DesignNodeKind.Solid, room.Id, ("text", "Desk"), ("shape", "box")))
            .Add(DesignNode.New(DesignNodeKind.Solid, room.Id, ("text", "Lamp"), ("shape", "cylinder")));

        return DesignMediums.Render(document);
    }

    /// <summary>
    /// The one that would have caught it. Everything the loop calls has to be ready before
    /// the loop runs, and `var` hoisting means the engine fails at the first thing rather
    /// than refusing to load — which looks exactly like a machine with no GPU.
    /// </summary>
    [Fact]
    public void The_floors_are_worked_out_before_anything_is_drawn()
    {
        var html = RoomWithThingsInIt();

        var storeys = html.IndexOf("var storeys =", StringComparison.Ordinal);
        var drawing = html.IndexOf("for (var i = 0; i < room.things.length; i++)", StringComparison.Ordinal);

        Assert.True(storeys > 0, "the storeys table is not in the script at all");
        Assert.True(drawing > 0, "the drawing loop is not in the script at all");

        Assert.True(
            storeys < drawing,
            "the drawing loop runs before the storeys table is filled in, so riseOf reads "
            + "undefined and the engine falls over to the flat room at the first solid.");
    }

    /// <summary>
    /// The same rule for the other thing the loop calls, so a future tidy-up cannot move one
    /// back without this saying so.
    /// </summary>
    [Fact]
    public void And_so_is_which_floors_are_shown()
    {
        var html = RoomWithThingsInIt();

        Assert.True(
            html.IndexOf("function shown(level)", StringComparison.Ordinal)
            < html.IndexOf("for (var i = 0; i < room.things.length; i++)", StringComparison.Ordinal),
            "shown() is defined after the loop that calls it.");
    }

    /// <summary>
    /// The room still goes over as data the script can read. A camelCase/PascalCase mismatch
    /// broke this once before in exactly the same silent way.
    /// </summary>
    [Fact]
    public void The_room_goes_over_in_the_spelling_the_script_reads()
    {
        var html = RoomWithThingsInIt();

        Assert.Contains("\"things\":", html, StringComparison.Ordinal);
        Assert.Contains("\"shape\":", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Things\":", html, StringComparison.Ordinal);
    }
}
