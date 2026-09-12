using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// One document model, five renderers.
///
/// The document knows nothing about how it is drawn, so matching five media is
/// five files rather than five products — and changing your mind about what you
/// are making throws nothing away.
///
/// An earlier version of this comment said the projects this took inspiration
/// from each weld their renderer to their document, and that this is why none can
/// become another. That was written from README summaries and is wrong: Pascal
/// enforces "core is pure logic — no Three.js, no rendering" with a test that
/// fails the build, and Diffusion Studio's runtime is explicitly headless. They
/// separate the two exactly as this does.
///
/// The separation is therefore not the argument. It is the ordinary right answer,
/// arrived at independently, and the argument is the bar below.
///
/// What is deliberately absent from every one of them, because it is what makes
/// this usable by the people it is for: no timeline, no track stack, no waveform,
/// no node graph, no orbit-by-dragging, no keyframes.
/// </summary>
public sealed class DesignMediumTests
{
    private static DesignDocument Deck()
    {
        var design = DesignDocument.Blank(medium: DesignMedium.Deck);
        var slide = DesignNode.New(DesignNodeKind.Frame, null, ("text", "First slide"));
        return design.Add(slide)
            .Add(DesignNode.New(DesignNodeKind.Text, slide.Id, ("text", "Saturday at ten")));
    }

    // ── What a document is ────────────────────────────────────────────────

    [Fact]
    public void A_page_has_no_frames()
    {
        Assert.Empty(DesignDocument.Blank().Frames);
        Assert.Equal(DesignMedium.Page, DesignDocument.Blank().Medium);
    }

    [Fact]
    public void A_deck_is_a_sequence_of_slides()
    {
        var deck = Deck().Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Second slide")));

        Assert.Equal(2, deck.Frames.Count);
        Assert.Equal("First slide", deck.Frames[0].Text);
    }

    /// <summary>
    /// The point of separating what a thing is from how it is drawn: changing your
    /// mind costs nothing, so nobody has to decide up front what they are making.
    /// </summary>
    [Fact]
    public void Changing_what_it_is_throws_nothing_away()
    {
        var deck = Deck();

        var video = deck.As(DesignMedium.Motion);

        Assert.Equal(DesignMedium.Motion, video.Medium);
        Assert.Equal(deck.Nodes.Count, video.Nodes.Count);
        Assert.Single(video.Frames);
    }

    /// <summary>
    /// The half of "throws nothing away" that was only true of the data.
    ///
    /// A page keeps its content loose on the root, so turning one into slides found
    /// no frames and drew "No slides yet" over a design that was still entirely
    /// there. The node count was right and the screen was empty, which is the worse
    /// of the two to get wrong.
    /// </summary>
    [Fact]
    public void A_page_turned_into_slides_still_shows_what_was_on_it()
    {
        var page = DesignDocument.Blank()
            .Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Sports Day")));

        var html = DesignMediums.Render(page.As(DesignMedium.Deck));

        Assert.Contains("Sports Day", html);
        Assert.DoesNotContain("No slides yet", html);
        Assert.Contains("1 of 1", html);
    }

    [Fact]
    public void And_as_a_video_and_a_room_too()
    {
        var page = DesignDocument.Blank()
            .Add(DesignNode.New(DesignNodeKind.Text, null, ("text", "Saturday at ten")));

        Assert.Contains("Saturday at ten", DesignMediums.Render(page.As(DesignMedium.Motion)));
        Assert.Contains("Saturday at ten", DesignMediums.Render(page.As(DesignMedium.Scene)));
        Assert.Contains("Saturday at ten", DesignMediums.Render(page.As(DesignMedium.Sound)));
    }

    /// <summary>
    /// Genuinely empty still says what to do. "Nothing here" and "your design
    /// vanished" must not look the same.
    /// </summary>
    [Fact]
    public void An_empty_design_still_says_what_to_do()
        => Assert.Contains("add a slide",
            DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Deck)));

    /// <summary>
    /// A label in a room lies on the floor rather than standing up. Standing them
    /// up was the obvious thing and the wrong one — the room is seen at a steep
    /// tilt, so a vertical plane rendered 112 by 0: in the DOM, invisible on screen.
    /// </summary>
    [Fact]
    public void Words_in_a_room_lie_on_the_floor_where_they_can_be_seen()
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));
        scene = scene.Add(room).Add(DesignNode.New(DesignNodeKind.Text, room.Id, ("text", "Table goes here")));

        var html = DesignMediums.Render(scene);

        Assert.Contains("Table goes here", html);
        Assert.DoesNotContain("rotateX(-90deg)", html);
    }

    [Fact]
    public void A_look_survives_changing_the_medium()
        => Assert.Equal("Night", Deck().Wearing("Night").As(DesignMedium.Sound).Look);

    /// <summary>One word each, and it is the one the person just used.</summary>
    [Fact]
    public void Each_medium_names_its_own_pieces()
    {
        Assert.Equal("slide", DesignMediums.PieceOf(DesignMedium.Deck));
        Assert.Equal("shot", DesignMediums.PieceOf(DesignMedium.Motion));
        Assert.Equal("room", DesignMediums.PieceOf(DesignMedium.Scene));
        Assert.Equal("track", DesignMediums.PieceOf(DesignMedium.Sound));
    }

    /// <summary>
    /// Every medium is offered, named in a word somebody would say, and every one
    /// says what it is for. The count is asserted against the enum rather than a
    /// number typed here, because a number typed here is a number that goes stale
    /// the moment a medium is added — which is exactly what happened when Phone
    /// and Board arrived.
    /// </summary>
    [Fact]
    public void Every_medium_is_named_in_words_people_use()
    {
        Assert.Equal(Enum.GetValues<DesignMedium>().Length, DesignMediums.All.Count);

        foreach (var medium in Enum.GetValues<DesignMedium>())
        {
            Assert.Contains(DesignMediums.All, m => m.Medium == medium);
        }

        Assert.Contains(DesignMediums.All, m => m.Name == "Slides");
        Assert.Contains(DesignMediums.All, m => m.Name == "Video");
        Assert.Contains(DesignMediums.All, m => m.Name == "Sound");
        Assert.Contains(DesignMediums.All, m => m.Name == "Phone");
        Assert.Contains(DesignMediums.All, m => m.Name == "Board");

        Assert.All(DesignMediums.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Blurb)));
        Assert.All(DesignMediums.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Piece)));
    }

    // ── Slides ────────────────────────────────────────────────────────────

    [Fact]
    public void A_deck_renders_every_slide_with_one_showing()
    {
        var html = DesignMediums.Render(Deck().Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Second"))));

        Assert.Equal(2, html.Split("class=\"slide").Length - 1);
        Assert.Contains("slide on", html);
        Assert.Contains("1 of 2", html);
    }

    /// <summary>
    /// Clicking a thumbnail and correcting what is on it are the same gesture, so
    /// the slide holding the selection is the one that shows.
    /// </summary>
    [Fact]
    public void The_slide_holding_what_you_pointed_at_is_the_one_showing()
    {
        var deck = Deck();
        var second = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Second"));
        deck = deck.Add(second);

        var html = DesignMediums.Render(deck, second.Id);

        // The second slide's own section carries "on", and the first does not.
        // Asserted on the element rather than by comparing string positions, which
        // is the kind of test that passes for the wrong reason.
        Assert.Contains($"class=\"slide on picked\" data-node=\"{second.Id}\"", html);
        Assert.Contains($"class=\"slide\" data-node=\"{deck.Frames[0].Id}\"", html);
    }

    /// <summary>
    /// A deck becomes a handout by printing it, with no export step and nobody
    /// told about PDF settings.
    /// </summary>
    [Fact]
    public void A_deck_prints_one_slide_to_a_page()
        => Assert.Contains("break-after: page", DesignMediums.Render(Deck()));

    [Fact]
    public void An_empty_deck_says_what_to_do()
        => Assert.Contains("add a slide", DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Deck)));

    // ── Video ─────────────────────────────────────────────────────────────

    [Fact]
    public void Shots_carry_how_long_they_last()
    {
        var video = DesignDocument.Blank(medium: DesignMedium.Motion)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Opening"), ("seconds", "9")));

        var html = DesignMediums.Render(video);

        Assert.Contains("data-seconds=\"9\"", html);
        Assert.Contains("9 seconds", html);
    }

    [Fact]
    public void A_shot_with_no_length_gets_a_sensible_one()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Motion)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Opening"))));

        Assert.Contains($"data-seconds=\"{MotionRenderer.DefaultSeconds}\"", html);
    }

    /// <summary>
    /// **This test used to be called `There_is_no_timeline_to_get_wrong`, and the decision it
    /// encoded has been reversed by the owner.**
    ///
    /// It asserted a 3px progress bar and no timeline, on the grounds that a timeline is the
    /// most excluding thing in video software. That confused the display with the verb.
    /// Somebody trying to earn a living from a four-minute film needs to see its shape — and
    /// they will learn a timeline, because people learn far harder things for far less. What
    /// they will not tolerate is a cockpit with no way in.
    ///
    /// So there is a timeline now (see <c>TimelineTests</c>), and what survives from the
    /// original decision is the half that was always right: **nothing on it can be dragged.**
    /// A length changes by saying "make the shot four seconds", which works while driving and
    /// dragging does not.
    ///
    /// The progress bar went with it. Two things saying where you are is one too many.
    /// </summary>
    [Fact]
    public void Nothing_on_the_timeline_can_be_dragged()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Motion)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Opening"))));

        Assert.DoesNotContain("type=\"range\"", html);
        Assert.DoesNotContain("scrub", html);
        Assert.DoesNotContain("draggable", html);
        Assert.Contains("class=\"tl\"", html);
    }

    // ── Space ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_room_draws_its_solids_in_three_dimensions()
    {
        var scene = DesignDocument.Blank(medium: DesignMedium.Scene);
        var room = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"));
        scene = scene.Add(room)
            .Add(DesignNode.New(DesignNodeKind.Solid, room.Id,
                ("text", "Table"), ("width", "160"), ("depth", "90"), ("height", "70")));

        var html = DesignMediums.Render(scene);

        Assert.Contains("--w:160px", html);
        Assert.Contains("preserve-3d", html);
        Assert.Contains("Kitchen", html);
    }

    /// <summary>
    /// Four arrow keys are learnable in a second and cannot be done by accident.
    /// Dragging to orbit is the gesture nobody discovers and everybody fights.
    /// </summary>
    [Fact]
    public void You_look_around_with_arrow_keys_not_by_dragging()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Scene)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Kitchen"))));

        Assert.Contains("ArrowLeft", html);
        Assert.DoesNotContain("mousemove", html);
    }

    // ── Sound ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_sound_plays_rather_than_showing_an_icon()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Sound)
            .Add(DesignNode.New(DesignNodeKind.Sound, null,
                ("text", "The theme"), ("src", "data:audio/mpeg;base64,AAAA"))));

        Assert.Contains("<audio controls", html);
        Assert.Contains("The theme", html);
    }

    [Fact]
    public void Everything_can_be_played_end_to_end()
        => Assert.Contains("Play everything", DesignMediums.Render(
            DesignDocument.Blank(medium: DesignMedium.Sound)
                .Add(DesignNode.New(DesignNodeKind.Sound, null, ("text", "One")))));

    /// <summary>
    /// The same rule pictures have, for the same reason: a model can write any
    /// string into a source, and a file:// address would pull something off the
    /// machine into a document that might be shared.
    /// </summary>
    [Fact]
    public void Audio_from_somewhere_it_should_not_be_is_not_loaded()
    {
        Assert.True(DesignMediums.IsSafeAudio("data:audio/mpeg;base64,AAAA"));
        Assert.False(DesignMediums.IsSafeAudio("file:///C:/Users/me/private.mp3"));

        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Sound)
            .Add(DesignNode.New(DesignNodeKind.Sound, null, ("src", "file:///C:/private.mp3"))));

        Assert.DoesNotContain("file:///", html);
    }

    /// <summary>
    /// "Add some music" should work before anybody has thought about a running
    /// order, so a loose sound is a track too.
    /// </summary>
    [Fact]
    public void A_sound_added_before_any_running_order_still_counts()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Sound)
            .Add(DesignNode.New(DesignNodeKind.Sound, null, ("text", "Loose"))));

        Assert.Contains("Loose", html);
        Assert.DoesNotContain("Nothing to listen to", html);
    }

    // ── Saying it ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("slides", DesignMedium.Deck)]
    [InlineData("make it a video", DesignMedium.Motion)]
    [InlineData("space", DesignMedium.Scene)]
    [InlineData("turn it into sound", DesignMedium.Sound)]
    public void You_can_say_what_you_are_making(string said, DesignMedium expected)
    {
        var heard = DesignSpeech.Hear(DesignDocument.Blank(), said, null);

        Assert.True(heard.Understood);
        Assert.Equal(expected, heard.Document.Medium);
    }

    [Fact]
    public void Adding_a_slide_says_slide_back_to_you()
    {
        var heard = DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Deck), "add a slide", null);

        Assert.True(heard.Understood);
        Assert.Equal("Added a slide", heard.What);
        Assert.Single(heard.Document.Frames);
    }

    [Fact]
    public void Adding_a_shot_says_shot_back_to_you()
        => Assert.Equal("Added a shot", DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Motion), "add a shot", null).What);

    /// <summary>
    /// Somebody who has just made a slide and then says "add a title" means on
    /// that slide. Putting it beside the slides would be defensible and obviously
    /// wrong.
    /// </summary>
    [Fact]
    public void Something_added_after_a_slide_lands_on_that_slide()
    {
        var deck = DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Deck), "add a slide", null).Document;

        var after = DesignSpeech.Hear(deck, "add a title that says Sports Day", null).Document;

        Assert.Single(after.Frames);
        Assert.Single(after.ChildrenOf(after.Frames[0].Id));
    }

    /// <summary>
    /// A page is one surface with no sequence to add to. Saying so beats silently
    /// making a slide nothing will ever draw.
    /// </summary>
    [Fact]
    public void Asking_for_a_slide_on_a_page_says_what_to_do_first()
    {
        var heard = DesignSpeech.Hear(DesignDocument.Blank(), "add a slide", null);

        Assert.False(heard.Understood);
        Assert.Contains("Say 'slides'", heard.Reply!);
    }

    /// <summary>
    /// "Start again" means this one is wrong, not that you have changed your mind
    /// about making slides.
    /// </summary>
    [Fact]
    public void Starting_again_keeps_what_you_are_making()
    {
        var deck = DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Deck), "add a slide", null).Document;

        var heard = DesignSpeech.Hear(deck, "start again", null);

        Assert.Equal(DesignMedium.Deck, heard.Document.Medium);
        Assert.Empty(heard.Document.Frames);
    }
}
