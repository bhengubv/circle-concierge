using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Building a room by saying so.
///
/// **Found by asking "parity now?" and then driving the list instead of reading it.** Twenty
/// of the twenty-five design tools could only be reached by a model calling them, and the
/// model that ships did not call one in three attempts — so on this machine those twenty were
/// features nobody could use. Pascal's entire section of the feature list was in that state,
/// and the plainest evidence was that **"wall" was not a word this file knew**. Nor door,
/// window, ceiling, roof, or any word for a level.
///
/// Typing "add a wall" into a room did nothing anybody could see. That is not a small gap on
/// a surface whose whole claim is that a five-year-old and a ninety-seven-year-old can say
/// what they want and look at it.
///
/// The pieces themselves come from <see cref="RoomPieces"/> rather than being built a second
/// time here, so a said wall and a tool-built wall are the same wall.
/// </summary>
public sealed class SayingARoomTests
{
    private static DesignDocument ARoom()
        => DesignDocument.Blank(medium: DesignMedium.Scene);

    private static DesignHeard Say(DesignDocument design, string words, string? pointedAt = null)
        => DesignSpeech.Hear(design, words, pointedAt);

    /// <summary>
    /// The words people actually use. "Add" is not the only verb anybody reaches for about a
    /// wall, and somebody who has to discover that it is has been handed a syntax.
    /// </summary>
    [Theory]
    [InlineData("add a wall", "wall")]
    [InlineData("put up a wall", "wall")]
    [InlineData("build a wall", "wall")]
    [InlineData("lay a floor", "floor")]
    [InlineData("add a ceiling", "ceiling")]
    [InlineData("add a roof", "roof")]
    public void A_room_can_be_built_by_saying_so(string said, string shape)
    {
        var heard = Say(ARoom(), said);

        Assert.True(heard.Understood, $"'{said}' was not understood");

        var piece = heard.Document.Nodes.Values.Single(
            node => node.Props.TryGetValue("shape", out var s) && s == shape);

        Assert.Equal(DesignNodeKind.Solid, piece.Kind);
    }

    /// <summary>
    /// A said wall is the same wall `design_build` makes. The defaults live in one place
    /// precisely so these cannot drift apart — a wall two metres long, 240 tall, 12 thick.
    /// </summary>
    [Fact]
    public void And_a_said_wall_is_the_same_wall_the_tool_builds()
    {
        var heard = Say(ARoom(), "add a wall");

        var wall = heard.Document.Nodes.Values.Single(
            node => node.Props.TryGetValue("shape", out var s) && s == "wall");

        foreach (var (key, value) in RoomPieces.Props("wall"))
        {
            Assert.Equal(value, wall.Props[key]);
        }
    }

    /// <summary>
    /// A door goes into the wall that is there, and the hole belongs to the wall rather than
    /// standing beside it — which is the whole reason there is no CSG library in this repo.
    /// </summary>
    [Fact]
    public void A_door_goes_into_the_wall_that_is_already_there()
    {
        var withWall = Say(ARoom(), "add a wall").Document;
        var heard = Say(withWall, "put a door in the wall");

        Assert.True(heard.Understood);

        var wall = heard.Document.Nodes.Values.Single(
            node => node.Props.TryGetValue("shape", out var s) && s == "wall");
        var door = heard.Document.Nodes.Values.Single(
            node => node.Props.TryGetValue("shape", out var s) && s == "door");

        Assert.Equal(wall.Id, door.ParentId);
    }

    [Theory]
    [InlineData("add a window")]
    [InlineData("cut a door into it")]
    [InlineData("put a window in the wall")]
    public void And_the_ways_people_say_it(string said)
    {
        var withWall = Say(ARoom(), "add a wall").Document;

        Assert.True(Say(withWall, said).Understood, $"'{said}' was not understood");
    }

    /// <summary>
    /// With no wall, it says so. Before this the sentence fell through to a model, and on a
    /// machine whose model cannot act that means nothing happens and nothing explains why —
    /// which is exactly the state this whole change was written to get out of.
    /// </summary>
    [Fact]
    public void A_door_with_no_wall_says_so_rather_than_going_quiet()
    {
        var heard = Say(ARoom(), "put a door in the wall");

        Assert.False(heard.Understood);
        Assert.Contains("no wall", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And walls belong in a room. On a page the sentence says where it does belong rather
    /// than adding something no renderer will draw.
    /// </summary>
    [Fact]
    public void And_a_wall_on_a_page_says_where_walls_go()
    {
        var heard = DesignSpeech.Hear(DesignDocument.Blank(medium: DesignMedium.Page), "add a wall", null);

        Assert.False(heard.Understood);
        Assert.Contains("room", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The old vocabulary still works. "add a box" in a room has meant a solid on the floor
    /// since the medium became reachable at all, and a new branch in front of it must not
    /// swallow it.
    /// </summary>
    [Fact]
    public void And_the_sentences_that_already_worked_still_do()
    {
        var heard = Say(ARoom(), "add a sphere");

        Assert.True(heard.Understood);
        Assert.Contains(heard.Document.Nodes.Values, node => node.Kind == DesignNodeKind.Solid);
    }
}
