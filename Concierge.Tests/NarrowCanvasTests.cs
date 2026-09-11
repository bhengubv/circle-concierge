namespace Concierge.Tests;

/// <summary>
/// What the canvas keeps when the window is too narrow for everything.
///
/// **Found by making the window 700px wide and looking.** The seven media and the six looks
/// dropped their names and became unlabelled icons — thirteen little pictures, and no way to
/// tell Page from Slides from Space, or Calm from Night.
///
/// Hiding them all is the obvious thing and it is wrong for this product in particular. The
/// whole argument for calling a look "Calm" rather than "#F4F5F7" is that a five-year-old and
/// a ninety-seven-year-old can both say which one they want; thirteen icons hand that back.
/// The chosen one keeps its name now, so the screen always answers "what am I making, and how
/// does it look?" in words.
///
/// This reads the stylesheet, which is crude and is what is available: no test here renders
/// at a width or applies CSS. It pins the rule so the next tidy-up cannot quietly delete it.
/// </summary>
public sealed class NarrowCanvasTests
{
    private static string Stylesheet => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "Concierge.Shared.Components", "wwwroot", "concierge.css")));

    [Fact]
    public void On_a_narrow_screen_the_names_give_way_to_the_pictures()
        => Assert.Contains(".dz-look-name { display: none; }", Stylesheet, StringComparison.Ordinal);

    [Fact]
    public void But_whatever_is_chosen_keeps_its_name()
        => Assert.Contains(
            ".dz-look.is-on .dz-look-name { display: inline; }",
            Stylesheet,
            StringComparison.Ordinal);

    /// <summary>
    /// In that order, inside the narrow-screen block: a rule that restores the name has to
    /// come after the one that hides it, or it does nothing at all.
    /// </summary>
    [Fact]
    public void And_the_rule_that_brings_it_back_comes_after_the_one_that_hides_it()
    {
        var css = Stylesheet;

        var hides = css.IndexOf(".dz-look-name { display: none; }", StringComparison.Ordinal);
        var keeps = css.IndexOf(".dz-look.is-on .dz-look-name { display: inline; }", StringComparison.Ordinal);

        Assert.True(hides > 0 && keeps > hides, "the chosen name is restored before it is hidden");

        // And both inside the handheld block rather than applying everywhere.
        var narrow = css.IndexOf("@media (max-width: 760px)", StringComparison.Ordinal);

        Assert.True(narrow > 0 && narrow < hides, "the rules are outside the narrow-screen block");
    }
}
