using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// Filling in a panel on a board by saying so.
///
/// A board is a small name, an enormous number and which way it is moving. `design_panel`
/// could set all three and no sentence reached any of them — so the one medium built entirely
/// around a number could be given empty panels by talking and nothing else.
/// </summary>
public sealed class SayingABoardTests
{
    private static DesignDocument ABoard()
        => DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Board), "add a panel", null).Document;

    private static DesignHeard Say(DesignDocument design, string words, string? pointedAt = null)
        => DesignSpeech.Hear(design, words, pointedAt);

    private static string PropOf(DesignDocument design, string key)
        => design.Frames[^1].Props.TryGetValue(key, out var value) ? value : string.Empty;

    [Theory]
    [InlineData("set the panel to 48,200", "48,200")]
    [InlineData("make the panel 12", "12")]
    [InlineData("set this panel to £3.4m", "£3.4m")]
    public void A_panel_can_be_filled_in_by_saying_so(string said, string expected)
    {
        var heard = Say(ABoard(), said);

        Assert.True(heard.Understood, $"'{said}' was not understood");
        Assert.Equal(expected, PropOf(heard.Document, "value"));
    }

    /// <summary>
    /// **A blank is a real answer**, and the one this medium is built around: an invented
    /// metric is slop the moment it is invented, so "nothing" has to be sayable rather than
    /// only reachable by never filling the panel in.
    /// </summary>
    [Theory]
    [InlineData("set the panel to blank")]
    [InlineData("set the panel to nothing")]
    [InlineData("make the panel unknown")]
    public void And_a_blank_is_something_somebody_can_ask_for(string said)
    {
        var filled = Say(ABoard(), "set the panel to 48,200").Document;

        Assert.Equal(string.Empty, PropOf(Say(filled, said).Document, "value"));
    }

    [Theory]
    [InlineData("the panel is up 12%", "+12%")]
    [InlineData("the panel is down 3", "-3")]
    [InlineData("panel is steady", "steady")]
    [InlineData("the panel is flat", "steady")]
    public void And_which_way_it_is_moving(string said, string expected)
    {
        var heard = Say(ABoard(), said);

        Assert.True(heard.Understood, $"'{said}' was not understood");
        Assert.Equal(expected, PropOf(heard.Document, "change"));
    }

    /// <summary>
    /// Setting the number does not wipe the note, and setting the note does not wipe the
    /// number — the tool writes only the fields it was given, and the sentences must not be
    /// the thing that breaks that.
    /// </summary>
    [Fact]
    public void And_saying_one_thing_does_not_clear_the_others()
    {
        var filled = Say(ABoard(), "set the panel to 48,200").Document;
        var moved = Say(filled, "the panel is up 12%").Document;

        Assert.Equal("48,200", PropOf(moved, "value"));
        Assert.Equal("+12%", PropOf(moved, "change"));
    }

    /// <summary>
    /// The panel being pointed at wins over the last one.
    /// </summary>
    [Fact]
    public void And_the_panel_being_pointed_at_is_the_one_that_changes()
    {
        var two = Say(ABoard(), "add a panel").Document;
        var first = two.Frames[0];

        var heard = Say(two, "set the panel to 7", first.Id);

        Assert.Equal("7", heard.Document.Find(first.Id)!.Props["value"]);
        Assert.Equal(string.Empty, PropOf(heard.Document, "value"));
    }

    [Fact]
    public void With_no_panels_it_says_so()
    {
        var heard = Say(DesignDocument.Blank(medium: DesignMedium.Board), "set the panel to 4");

        Assert.False(heard.Understood);
        Assert.Contains("no panels", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void And_on_a_page_it_says_where_panels_live()
    {
        var heard = Say(DesignDocument.Blank(medium: DesignMedium.Page), "set the panel to 4");

        Assert.False(heard.Understood);
        Assert.Contains("board", heard.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **Every panel on a board was called "A PANEL".**
    ///
    /// The small name above the number is the frame's own text and nothing set it — "add a
    /// title saying Signups" puts a heading *inside* the panel instead. So a board of four
    /// panels read A PANEL four times over four different numbers, which is the one thing a
    /// board must never do: it is read at a glance by whoever is walking past.
    ///
    /// Found by building a board on the running app and looking at it.
    /// </summary>
    [Theory]
    [InlineData("call the panel Signups", "Signups")]
    [InlineData("name this panel Monthly churn", "Monthly churn")]
    public void A_panel_can_be_named(string said, string expected)
    {
        var heard = Say(ABoard(), said);

        Assert.True(heard.Understood, $"'{said}' was not understood");
        Assert.Equal(expected, PropOf(heard.Document, "text"));
    }

    /// <summary>
    /// And naming it does not clear what it says — the two are separate facts about one
    /// panel, and the sentences must not be what breaks that.
    /// </summary>
    [Fact]
    public void And_naming_it_keeps_its_number()
    {
        var filled = Say(ABoard(), "set the panel to 48,200").Document;
        var named = Say(filled, "call the panel Signups").Document;

        Assert.Equal("Signups", PropOf(named, "text"));
        Assert.Equal("48,200", PropOf(named, "value"));
    }

    /// <summary>
    /// **A panel showing 48,200 said "Nothing on this panel yet" directly underneath it.**
    ///
    /// A panel's number, direction and note are properties of the frame rather than children,
    /// and the only emptiness test counted children — so on the one medium built entirely
    /// around a number, the number was not counted as content. Seen on the running app the
    /// day panels became sayable.
    /// </summary>
    [Fact]
    public void A_panel_with_a_number_on_it_is_not_described_as_empty()
    {
        var filled = Say(ABoard(), "set the panel to 48,200").Document;

        var html = DesignMediums.Render(filled);

        Assert.Contains("48,200", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing on this panel yet", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a panel with genuinely nothing on it still says so — the empty state is right, it
    /// was only ever wrong when there was something to show. Same shape as the canvas restore
    /// defect, and worth pinning for the same reason.
    /// </summary>
    [Fact]
    public void But_a_panel_with_nothing_on_it_still_says_so()
        => Assert.Contains(
            "Nothing on this panel yet",
            DesignMediums.Render(ABoard()),
            StringComparison.Ordinal);
}
