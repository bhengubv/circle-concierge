using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Every medium, with something actually on it.
///
/// **This file exists because an empty case is not a case.** The 3D engine was ticked,
/// tested and shipped, and it threw at the first solid in the room for two months — because
/// the room it was verified with was empty. No things, no loop, no throw, and an honest
/// report saying it drew.
///
/// Every renderer here was tested the same way: the blank state, the invitation, the
/// "nothing here yet". So this is the other half, one test per medium, each asking the
/// question the empty test cannot: **with real content on it, is the content there?**
/// </summary>
public sealed class PopulatedMediaTests
{
    /// <summary>A frame with the four things a person actually puts on one.</summary>
    private static DesignDocument Full(DesignMedium medium)
    {
        var frame = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Sports Day"));

        return DesignDocument.Blank(medium: medium)
            .Add(frame)
            .Add(DesignNode.New(DesignNodeKind.Heading, frame.Id, ("text", "Saturday the 14th")))
            .Add(DesignNode.New(DesignNodeKind.Text, frame.Id, ("text", "Races start at ten.")))
            .Add(DesignNode.New(
                DesignNodeKind.Image, frame.Id,
                ("text", "The field"),
                ("src", "data:image/png;base64,iVBORw0KGgo=")))
            .Add(DesignNode.New(
                DesignNodeKind.Sound, frame.Id,
                ("text", "The whistle"),
                ("src", "data:audio/mpeg;base64,SUQz")));
    }

    /// <summary>
    /// What every renderer says when it has nothing. Finding one of these in a populated
    /// render means the content went missing on the way to the screen.
    /// </summary>
    private static readonly string[] Emptiness =
    [
        "Nothing here yet",
        "Nothing on this",
        "No shots yet",
        "No screens yet",
        "Nothing to show yet",
        "No slides yet",
    ];

    [Theory]
    [InlineData(DesignMedium.Page)]
    [InlineData(DesignMedium.Deck)]
    [InlineData(DesignMedium.Motion)]
    [InlineData(DesignMedium.Sound)]
    [InlineData(DesignMedium.Handheld)]
    [InlineData(DesignMedium.Board)]
    [InlineData(DesignMedium.Scene)]
    public void What_is_on_it_is_drawn_on_it(DesignMedium medium)
    {
        var html = DesignMediums.Render(Full(medium));

        Assert.Contains("Saturday the 14th", html, StringComparison.Ordinal);
        Assert.Contains("Races start at ten.", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DesignMedium.Page)]
    [InlineData(DesignMedium.Deck)]
    [InlineData(DesignMedium.Motion)]
    [InlineData(DesignMedium.Sound)]
    [InlineData(DesignMedium.Handheld)]
    [InlineData(DesignMedium.Board)]
    public void A_picture_and_a_sound_are_drawn_rather_than_named(DesignMedium medium)
    {
        var html = DesignMediums.Render(Full(medium));

        // The picture as a picture, and the sound as something that plays — not a caption
        // saying one is there. A placeholder box where a real picture was given is the
        // quiet failure this is looking for.
        Assert.Contains("<img", html, StringComparison.Ordinal);
        Assert.Contains("<audio", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DesignMedium.Page)]
    [InlineData(DesignMedium.Deck)]
    [InlineData(DesignMedium.Motion)]
    [InlineData(DesignMedium.Sound)]
    [InlineData(DesignMedium.Handheld)]
    [InlineData(DesignMedium.Board)]
    [InlineData(DesignMedium.Scene)]
    public void Nothing_says_there_is_nothing_on_it(DesignMedium medium)
    {
        var html = DesignMediums.Render(Full(medium));

        foreach (var empty in Emptiness)
        {
            Assert.DoesNotContain(empty, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The claim the whole surface rests on: changing what you are making throws nothing
    /// away. Tested with something on it rather than with a blank document, because a blank
    /// document survives any amount of losing things.
    /// </summary>
    [Fact]
    public void Changing_what_is_being_made_keeps_what_is_on_it_in_every_direction()
    {
        var from = Full(DesignMedium.Page);

        foreach (var medium in Enum.GetValues<DesignMedium>())
        {
            var html = DesignMediums.Render(from.As(medium));

            Assert.Contains("Saturday the 14th", html, StringComparison.Ordinal);
        }
    }

    // ── A room with things in it ──────────────────────────────────────────

    /// <summary>
    /// A room carrying every shape it offers, because the shapes are drawn by different code
    /// and the one that threw was only reached by the second of them.
    /// </summary>
    private static DesignDocument ARoom()
    {
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "The office"));

        var document = DesignDocument.Blank(medium: DesignMedium.Scene).Add(room);

        foreach (var (shape, name) in new[]
                 {
                     ("box", "Desk"), ("sphere", "A ball"), ("cylinder", "Lamp"), ("cone", "A cone"),
                 })
        {
            document = document.Add(DesignNode.New(
                DesignNodeKind.Solid, room.Id,
                ("text", name), ("shape", shape), ("width", "80"), ("depth", "80"), ("height", "80")));
        }

        return document.Add(DesignNode.New(
            DesignNodeKind.Solid, room.Id,
            ("text", "North wall"), ("shape", "wall"),
            ("x", "-250"), ("y", "-250"), ("x2", "250"), ("y2", "-250"),
            ("height", "260"), ("thickness", "12")));
    }

    [Fact]
    public void A_room_carries_every_thing_in_it_over_to_the_engine()
    {
        var html = DesignMediums.Render(ARoom());

        foreach (var name in new[] { "Desk", "A ball", "Lamp", "A cone", "North wall" })
        {
            Assert.Contains(name, html, StringComparison.Ordinal);
        }

        foreach (var shape in new[] { "box", "sphere", "cylinder", "cone", "wall" })
        {
            Assert.Contains($"\"shape\":\"{shape}\"", html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The flat room is drawn first and the engine draws over it, so a room that reaches the
    /// screen has both. The fallback going missing would leave somebody on an old laptop
    /// looking at an empty rectangle.
    /// </summary>
    [Fact]
    public void And_the_flat_room_is_still_drawn_underneath_it()
    {
        var html = DesignMediums.Render(ARoom());

        Assert.Contains("class=\"solid", html, StringComparison.Ordinal);
        Assert.Contains("<canvas", html, StringComparison.Ordinal);
    }
}
