using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// The ordinary sentences, understood without a model.
///
/// Not a replacement for one — a model handles "make it feel like a school
/// newsletter". This handles "add a title that says Sports Day", which is the
/// sentence people type first and which should not require a language model, a
/// network, or a loaded engine.
///
/// It earns its place three times over: the surface has to be usable while the
/// engine is loading, offline, or broken (on this machine currently all three);
/// it is instant, and for a five-year-old the gap between fifteen milliseconds
/// and four seconds is the whole experience; and it is predictable, which is what
/// somebody needs while still learning what they can say.
/// </summary>
public sealed class DesignSpeechTests
{
    private static DesignDocument Blank() => DesignDocument.Blank();

    private static DesignHeard Say(string words, DesignDocument? design = null, string? pointing = null)
        => DesignSpeech.Hear(design ?? Blank(), words, pointing);

    // ── Adding things ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("add a title that says Sports Day")]
    [InlineData("add a heading: Sports Day")]
    [InlineData("put a title saying Sports Day")]
    [InlineData("make a header with Sports Day")]
    public void The_ways_people_ask_for_a_title_all_work(string said)
    {
        var heard = Say(said);

        Assert.True(heard.Understood);
        var node = Assert.Single(heard.Document.ChildrenOf(heard.Document.RootId));
        Assert.Equal(DesignNodeKind.Heading, node.Kind);
        Assert.Equal("Sports Day", node.Text);
    }

    [Fact]
    public void Words_buttons_and_pictures_are_all_askable_for()
    {
        Assert.Equal(DesignNodeKind.Text, Only(Say("add some words saying Saturday at ten")).Kind);
        Assert.Equal(DesignNodeKind.Button, Only(Say("add a button that says Sign up")).Kind);
        Assert.Equal(DesignNodeKind.Image, Only(Say("add a picture")).Kind);
        Assert.Equal(DesignNodeKind.Box, Only(Say("add a box")).Kind);
    }

    /// <summary>Nobody wants a heading that reads "Sports Day". with the quotes.</summary>
    [Fact]
    public void Quotes_and_full_stops_people_type_are_not_kept()
    {
        Assert.Equal("Sports Day", Only(Say("add a title that says \"Sports Day\".")).Text);
    }

    /// <summary>
    /// The history strip is read by somebody deciding which version they liked, so
    /// what happened is described in the words they would use.
    /// </summary>
    [Fact]
    public void What_happened_is_said_in_plain_words()
    {
        Assert.Equal("Added a title", Say("add a title that says Hello").What);
        Assert.Equal("Added a picture", Say("add a picture").What);
    }

    // ── How it looks ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("make it night")]
    [InlineData("use the night look")]
    [InlineData("night")]
    public void A_look_can_be_asked_for_however_it_is_phrased(string said)
    {
        var heard = Say(said);

        Assert.True(heard.Understood);
        Assert.Equal("Night", heard.Document.Look);
    }

    // ── Pointing supplies the subject ─────────────────────────────────────

    /// <summary>
    /// The whole reason pointing is first-class: "make this bigger" only means
    /// something because the person is touching the thing they mean.
    /// </summary>
    [Fact]
    public void Bigger_applies_to_whatever_is_being_touched()
    {
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        var design = Blank().Add(heading);

        var heard = Say("make it bigger", design, heading.Id);

        Assert.True(heard.Understood);
        Assert.Equal("big", heard.Document.Find(heading.Id)!.Props["size"]);
        Assert.Equal("Made the title bigger", heard.What);
    }

    [Fact]
    public void Changing_what_something_says_needs_you_to_be_touching_it()
    {
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Before"));
        var design = Blank().Add(heading);

        var heard = Say("change it to say After", design, heading.Id);

        Assert.Equal("After", heard.Document.Find(heading.Id)!.Text);
    }

    /// <summary>
    /// Said with nothing touched, this is asked about rather than guessed at.
    /// Guessing which thing somebody meant is how a surface deletes the wrong one.
    /// </summary>
    [Fact]
    public void Without_pointing_it_says_what_to_do_rather_than_guessing()
    {
        var heard = Say("make it bigger");

        Assert.False(heard.Understood);
        Assert.Contains("Touch the thing", heard.Reply!);
    }

    // ── Removing, carefully ───────────────────────────────────────────────

    [Fact]
    public void Delete_removes_what_is_being_touched()
    {
        var heading = DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title"));
        var design = Blank().Add(heading);

        var heard = Say("delete", design, heading.Id);

        Assert.True(heard.Understood);
        Assert.True(heard.Document.IsEmpty);
    }

    /// <summary>
    /// The one mistake this surface must never make. A sentence that deletes
    /// something nobody was touching breaks the promise the whole thing rests on:
    /// try it and see.
    /// </summary>
    [Fact]
    public void Delete_with_nothing_touched_removes_nothing()
    {
        var design = Blank().Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Precious")));

        var heard = Say("delete", design);

        Assert.False(heard.Understood);
        Assert.Single(heard.Document.ChildrenOf(heard.Document.RootId));
    }

    [Fact]
    public void Starting_again_empties_the_page_and_keeps_the_look()
    {
        var design = Blank().Wearing("Warm").Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Old")));

        var heard = Say("start again", design);

        Assert.True(heard.Understood);
        Assert.True(heard.Document.IsEmpty);
        Assert.Equal("Warm", heard.Document.Look);
    }

    // ── Not understanding, out loud ───────────────────────────────────────

    /// <summary>
    /// A surface that silently ignores a sentence teaches people it is broken.
    /// Saying so, with an example, teaches them what they can say.
    /// </summary>
    [Fact]
    public void What_it_cannot_read_it_says_so_about_with_an_example()
    {
        var heard = Say("please render a photorealistic sunset over Table Mountain");

        Assert.False(heard.Understood);
        Assert.Contains("add a title that says", heard.Reply!);
    }

    [Fact]
    public void An_empty_sentence_does_nothing_and_says_nothing()
    {
        var heard = Say("   ");

        Assert.False(heard.Understood);
        Assert.Null(heard.Reply);
    }

    /// <summary>Nothing it fails to understand may change the design.</summary>
    [Fact]
    public void A_sentence_it_cannot_read_leaves_the_design_exactly_as_it_was()
    {
        var design = Blank().Add(DesignNode.New(DesignNodeKind.Heading, null, ("text", "Title")));

        var heard = Say("hjkl asdf", design);

        Assert.Same(design, heard.Document);
    }

    /// <summary>
    /// The nasty one. Scanning the whole sentence for a look name meant "add a
    /// title that says Night Market" changed the look and added nothing — and a
    /// surface that does something adjacent to what you asked is worse than one
    /// that admits it did not understand.
    /// </summary>
    [Fact]
    public void A_look_name_inside_a_title_is_not_a_request_to_change_the_look()
    {
        var heard = Say("add a title that says Night Market");

        Assert.True(heard.Understood);
        Assert.Equal("Night Market", Only(heard).Text);
        Assert.Equal("Calm", heard.Document.Look);
    }

    /// <summary>
    /// And the other half of the same bug: "saying" was matched as "say" plus
    /// "ing", so this produced a heading reading "ing Sports Day" — understood,
    /// wrong, and silent about being wrong.
    /// </summary>
    [Fact]
    public void Saying_is_not_read_as_say_plus_ing()
        => Assert.Equal("Sports Day", Only(Say("put a title saying Sports Day")).Text);

    private static DesignNode Only(DesignHeard heard)
        => Assert.Single(heard.Document.ChildrenOf(heard.Document.RootId));
}
