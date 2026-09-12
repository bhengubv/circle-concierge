namespace Concierge.Tests;

/// <summary>
/// The canvas being the application rather than a preview pane.
///
/// **Measured on the running desktop head at 1350x680: the drawing was 1024x341.** Thirty-eight
/// per cent of the window and half its height, because the canvas was one row of a four-row
/// stack — a caption bar, the drawing, a strip of history, then two rows of pickers.
///
/// Everything except the drawing floats over its edges now, and the controls rest as two
/// pills — what you are making, and how it looks — opening to everything on hover. Same
/// measurement afterwards: 1053x562, and 432px of it unobscured against 341 before.
///
/// This reads the stylesheet, which is crude and is what is available: nothing here renders at
/// a width or applies CSS. It pins the rules so the next tidy-up cannot quietly put the stack
/// back.
/// </summary>
public sealed class CanvasHasTheRoomTests
{
    private static string Stylesheet => File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "Concierge.Shared.Components", "wwwroot", "concierge.css")));

    /// <summary>
    /// The drawing fills the canvas rather than sharing a grid with three other rows.
    /// </summary>
    [Fact]
    public void The_drawing_takes_the_whole_surface()
    {
        var css = Stylesheet;

        Assert.Contains(".dz-canvas { position: absolute; inset: 0;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-rows: auto minmax(0, 1fr) auto auto;", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the controls are over it, costing no height.
    /// </summary>
    [Fact]
    public void And_the_controls_float_over_it()
    {
        var css = Stylesheet;

        Assert.Contains(".dz-controls {", css, StringComparison.Ordinal);
        Assert.Contains("bottom: 0;", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// **What you are making and how it looks stay in words.** That is the whole argument for
    /// calling a look "Calm" rather than "#F4F5F7", and the same rule the narrow-window
    /// behaviour follows — so collapsing the controls must not take it away.
    /// </summary>
    [Fact]
    public void But_the_chosen_medium_and_look_are_still_on_screen()
    {
        var css = Stylesheet;

        var hides = css.IndexOf(".dz-controls .dz-look:not(.is-on) { display: none; }", StringComparison.Ordinal);

        Assert.True(hides > 0, "the collapsed state does not hide the unchosen pills");

        // Only the unchosen ones. A rule that hid `.dz-look` outright would leave somebody
        // with no answer to "what am I making?" at all.
        Assert.DoesNotContain(".dz-controls .dz-look { display: none; }", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// And everything comes back on hover or focus — focus as well as hover, because a
    /// keyboard reaches these and a mouse is not the only way in.
    /// </summary>
    [Fact]
    public void And_everything_returns_on_hover_or_focus()
    {
        var css = Stylesheet;

        Assert.Contains(".dz-controls:hover .dz-look", css, StringComparison.Ordinal);
        Assert.Contains(".dz-controls:focus-within .dz-look", css, StringComparison.Ordinal);
        Assert.Contains(".dz-controls:hover .dz-past", css, StringComparison.Ordinal);
        Assert.Contains(".dz-controls:focus-within .dz-past", css, StringComparison.Ordinal);
    }
}
