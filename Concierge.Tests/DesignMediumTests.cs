using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// One document model, five renderers.
///
/// The six projects this took inspiration from are six products: a page tool, a
/// deck tool, two video tools, a 3D tool, a music tool. Each hard-wires its
/// renderer into its document, which is why none of them can become another one.
///
/// Here the document knows nothing about how it is drawn, so matching all five is
/// five files rather than five products — and changing your mind about what you
/// are making throws nothing away.
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

    [Fact]
    public void The_five_are_named_in_words_people_use()
    {
        Assert.Equal(5, DesignMediums.All.Count);
        Assert.Contains(DesignMediums.All, m => m.Name == "Slides");
        Assert.Contains(DesignMediums.All, m => m.Name == "Video");
        Assert.Contains(DesignMediums.All, m => m.Name == "Sound");
        Assert.All(DesignMediums.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Blurb)));
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
    /// The decision this renderer turns on. A timeline is the single most
    /// excluding thing in video software — scrubbing, tracks, keyframes, a
    /// playhead — and what replaces it is a progress bar that cannot be dragged.
    /// </summary>
    [Fact]
    public void There_is_no_timeline_to_get_wrong()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Motion)
            .Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", "Opening"))));

        Assert.DoesNotContain("range", html);
        Assert.DoesNotContain("scrub", html);
        Assert.Contains("class=\"bar\"", html);
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
