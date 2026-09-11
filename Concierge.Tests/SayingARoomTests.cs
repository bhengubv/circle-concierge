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

    /// <summary>
    /// Looking at a building floor by floor — the one thing a model of a building does that a
    /// physical model cannot, and it was reachable only by a tool call.
    /// </summary>
    [Theory]
    [InlineData("pull the floors apart", "apart")]
    [InlineData("show them apart", "apart")]
    [InlineData("show the whole building", "whole")]
    [InlineData("show one floor", "one")]
    [InlineData("show floor 2", "one")]
    public void A_building_can_be_pulled_apart_by_saying_so(string said, string showing)
    {
        var withRoom = Say(ARoom(), "add a room").Document;
        var heard = Say(withRoom, said);

        Assert.True(heard.Understood, $"'{said}' was not understood");

        var room = DesignMediums.FramesOf(heard.Document)[^1];

        Assert.Equal(showing, room.Props["showing"]);
    }

    /// <summary>
    /// And the floor asked for is the floor shown, rather than always the ground one.
    /// </summary>
    [Fact]
    public void And_the_floor_asked_for_is_the_one_shown()
    {
        var withRoom = Say(ARoom(), "add a room").Document;
        var heard = Say(withRoom, "show floor 2");

        Assert.Equal("2", DesignMediums.FramesOf(heard.Document)[^1].Props["only"]);
    }

    /// <summary>
    /// With nothing built, it says so rather than going quiet — the same rule as a door with
    /// no wall.
    /// </summary>
    [Fact]
    public void And_with_no_building_it_says_so()
    {
        var heard = Say(ARoom(), "pull the floors apart");

        Assert.False(heard.Understood);
        Assert.Contains("no building", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Furniture by name, from the catalogue anybody can add to.
    ///
    /// `design_furnish` was reachable only by a model, and "add a desk" is about the most
    /// natural sentence there is in a room. The catalogue decides the words rather than this
    /// file, so a name added to `shapes.json` is sayable the same day with no code change —
    /// which is the whole point of the file existing.
    /// </summary>
    [Theory]
    [InlineData("add a desk")]
    [InlineData("put in a chair")]
    [InlineData("put a lamp in the room")]
    public void Furniture_can_be_asked_for_by_name(string said)
    {
        var heard = DesignSpeech.Hear(ARoom(), said, null, Catalogue());

        Assert.True(heard.Understood, $"'{said}' was not understood");
        Assert.Contains(heard.Document.Nodes.Values, node => node.Kind == DesignNodeKind.Solid);
    }

    /// <summary>
    /// A said desk is the desk the tool places — same parts, same sizes.
    /// </summary>
    [Fact]
    public void And_a_said_desk_is_the_desk_the_tool_places()
    {
        var said = DesignSpeech.Hear(ARoom(), "add a desk", null, Catalogue()).Document;
        var built = RoomPieces.Furnish(ARoom(), null, Catalogue().Find("desk")!);

        Assert.Equal(built.Nodes.Count, said.Nodes.Count);
    }

    /// <summary>
    /// Something the catalogue has never heard of comes back with the list, because somebody
    /// who guessed once will guess again — and the whole catalogue is a handful of names.
    /// </summary>
    [Fact]
    public void And_something_that_is_not_in_it_is_told_what_is()
    {
        var heard = DesignSpeech.Hear(ARoom(), "add a harpsichord", null, Catalogue());

        Assert.False(heard.Understood);
        Assert.Contains("desk", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And with no catalogue wired at all it says that instead — a host that cannot furnish a
    /// room and a room with no such furniture are different facts.
    /// </summary>
    [Fact]
    public void And_no_catalogue_at_all_is_a_different_answer()
    {
        var heard = DesignSpeech.Hear(ARoom(), "add a desk", null);

        Assert.False(heard.Understood);
        Assert.Contains("Nothing is set up", heard.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The catalogue must never swallow a word the canvas already knows. Putting the
    /// furniture branch in front of the noun list turned "add a sphere" into "there is
    /// nothing called sphere" and took nine tests red at once.
    /// </summary>
    [Theory]
    [InlineData("add a sphere")]
    [InlineData("add a box")]
    [InlineData("add a wall")]
    public void And_known_words_still_win_over_the_catalogue(string said)
    {
        var heard = DesignSpeech.Hear(ARoom(), said, null, Catalogue());

        Assert.True(heard.Understood, $"'{said}' was not understood");
        Assert.DoesNotContain("nothing called", heard.Reply ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The shipped catalogue, read from where the app reads it.</summary>
    private static RoomCatalogue Catalogue()
        => new(Path.Combine(Path.GetTempPath(), $"shapes-{Guid.NewGuid():N}.json"));

    /// <summary>
    /// Two pieces of furniture do not stand in the same place.
    ///
    /// **Watched this happen on the desktop head**: a desk, a chair and a lamp said one after
    /// another all landed at the middle of the floor, inside one another, and the room looked
    /// as though only the last had been added. The same defect `DesignSpeech.Standing` was
    /// written for, arriving a second time through a different door — which is what happens
    /// when a layout rule lives in one of two places that both place things.
    /// </summary>
    [Fact]
    public void Two_things_put_in_a_room_do_not_stand_in_the_same_place()
    {
        var catalogue = Catalogue();

        var one = DesignSpeech.Hear(ARoom(), "add a desk", null, catalogue).Document;
        var two = DesignSpeech.Hear(one, "add a chair", null, catalogue).Document;

        var spots = two.Nodes.Values
            .Where(node => node.Kind == DesignNodeKind.Box)
            .Select(node => (node.Props["x"], node.Props["y"]))
            .ToList();

        Assert.Equal(2, spots.Count);
        Assert.Equal(2, spots.Distinct().Count());
    }

    /// <summary>
    /// And a tool that says exactly where still gets exactly there — the arrangement is for
    /// when nobody said, not instead of what somebody said.
    /// </summary>
    [Fact]
    public void And_a_place_that_was_asked_for_is_still_the_place()
    {
        var placed = RoomPieces.Furnish(ARoom(), null, Catalogue().Find("desk")!, 42, 7);

        var thing = placed.Nodes.Values.Single(node => node.Kind == DesignNodeKind.Box);

        Assert.Equal("42", thing.Props["x"]);
        Assert.Equal("7", thing.Props["y"]);
    }
}
