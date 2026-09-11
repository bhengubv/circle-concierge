using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A word longer than the page.
///
/// **Found by pasting a long URL and a Welsh village into a design and looking**: the canvas
/// grew a horizontal scrollbar and the text ran off the right edge. A design that scrolls
/// sideways is a design nobody can read, and "no horizontal overflow" is one of the standing
/// checks this whole redesign was measured against.
///
/// It is worse than it looks on screen, too: the same HTML is what `design_save` writes out,
/// so a page sent to somebody else arrives broken in their browser as well.
/// </summary>
public sealed class LongWordTests
{
    private const string TooLong =
        "Llanfairpwllgwyngyllgogerychwyrndrobwllllantysiliogogogoch"
        + "Llanfairpwllgwyngyllgogerychwyrndrobwllllantysiliogogogoch";

    private static DesignDocument With(DesignMedium medium)
    {
        var frame = DesignNode.New(DesignNodeKind.Frame, null, ("text", "A frame"));

        return DesignDocument.Blank(medium: medium)
            .Add(frame)
            .Add(DesignNode.New(DesignNodeKind.Text, frame.Id, ("text", TooLong)));
    }

    /// <summary>
    /// Every medium, because they share one head and the page has its own — so a fix in one
    /// place would have left the other six exactly as they were.
    /// </summary>
    [Theory]
    [InlineData(DesignMedium.Page)]
    [InlineData(DesignMedium.Deck)]
    [InlineData(DesignMedium.Motion)]
    [InlineData(DesignMedium.Sound)]
    [InlineData(DesignMedium.Handheld)]
    [InlineData(DesignMedium.Board)]
    [InlineData(DesignMedium.Scene)]
    public void A_word_longer_than_the_page_is_broken_rather_than_pushing_the_page_wider(
        DesignMedium medium)
    {
        var html = DesignMediums.Render(With(medium));

        Assert.Contains("overflow-wrap: anywhere", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the page renderer's own stylesheet says it too, because a page does not use the
    /// shared head — which is exactly how a rule like this ends up applying to six media and
    /// not the seventh.
    /// </summary>
    [Fact]
    public void Including_the_page_which_has_a_stylesheet_of_its_own()
    {
        var html = DesignMediums.Render(With(DesignMedium.Page));

        var head = html.IndexOf("</style>", StringComparison.Ordinal);
        var rule = html.IndexOf("overflow-wrap: anywhere", StringComparison.Ordinal);

        Assert.True(rule > 0 && rule < head, "the rule is not in the page's own stylesheet");
    }

    /// <summary>
    /// The word itself is still all there. Breaking where it wraps must not mean breaking
    /// what it says.
    /// </summary>
    [Fact]
    public void And_the_word_is_still_the_word()
        => Assert.Contains(TooLong, DesignMediums.Render(With(DesignMedium.Page)), StringComparison.Ordinal);
}
