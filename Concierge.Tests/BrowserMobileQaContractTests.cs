namespace Concierge.Tests;

public sealed class BrowserMobileQaContractTests
{
    private static readonly string[] Routes =
    [
        // "/" is the workspace and the workspace is Chat.razor. Home.razor kept
        // its name but lost its route — it is a holder for camera and voice
        // interop that renders no markup, so requiring a heading of it would
        // be requiring a heading of nothing.
        "Chat.razor",
        "Skills.razor",
        "Settings.razor",
        "Approvals.razor",
        "Release.razor",
        "Roadmap.razor",
        "BusinessApis.razor",
        "Engineering.razor",
        "Product.razor",
        "Beyond.razor",
        "Pricing.razor"
    ];

    [Theory]
    [InlineData("360", "740", "mobile")]
    [InlineData("768", "1024", "tablet")]
    [InlineData("1440", "900", "desktop")]
    public void Required_browser_qa_viewports_are_tracked(string width, string height, string label)
    {
        Assert.False(string.IsNullOrWhiteSpace(width));
        Assert.False(string.IsNullOrWhiteSpace(height));
        Assert.False(string.IsNullOrWhiteSpace(label));
    }

    /// <summary>
    /// The sidebar responds to the window, and no scrollbar is ever visible.
    ///
    /// This read MainLayout.razor.css and NavMenu.razor.css, which no longer
    /// exist — the per-component stylesheets were replaced by one sheet, and
    /// the breakpoint moved from 641px to 760px, which is where the sidebar
    /// and a usable thread actually stop fitting side by side. The guarantee
    /// is unchanged; the file and the number are not.
    ///
    /// Note what this deliberately does NOT claim any more: see the skipped
    /// test below.
    /// </summary>
    [Fact]
    public void The_sidebar_responds_to_the_window_and_no_scrollbar_shows()
    {
        var root = FindWorkspaceRoot();
        var css = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "wwwroot", "concierge.css"));

        Assert.Contains("@media (min-width: 760px) { .ws-side { display: flex; } }", css, StringComparison.Ordinal);

        // Scrolling still works; the bar must not be visible. Hidden globally
        // rather than per component, which is why collapsible groups carry the
        // job of keeping a long list inside the window.
        Assert.Contains("scrollbar-width: none", css, StringComparison.Ordinal);
        Assert.Contains("::-webkit-scrollbar", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// Below 760px the sidebar is display:none and nothing replaces it, so
    /// there is no way to reach a thread, a room, or the approval queue on a
    /// phone. The old bottom tab bar was deleted with the rest of the UI and
    /// desktop was made the priority deliberately.
    ///
    /// This is skipped rather than absent because the previous version of this
    /// test was named "..._for_mobile_and_desktop" and passed, which asserted
    /// that mobile worked while nothing checked that it did. A skipped test
    /// says the gap is known; a deleted one says nobody thought of it.
    /// </summary>
    [Fact(Skip = "Mobile navigation does not exist below 760px — desktop first, by decision.")]
    public void Mobile_has_a_way_to_reach_threads_and_rooms()
    {
        Assert.Fail("No navigation is rendered below the 760px breakpoint.");
    }

    [Fact]
    public void Every_core_page_has_a_plain_header_or_workspace_intro()
    {
        var root = FindWorkspaceRoot();

        foreach (var route in Routes)
        {
            var page = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Pages", route));
            // This used to be an allowlist of class names, and it grew by one
            // entry every time the design changed — page-header, then hero,
            // then agent-stage, then home-hero, then cu-page-title. That made
            // it a test of which stylesheet was current rather than of what it
            // was actually protecting, which is that a page opens with a
            // visible title you can see and a screen reader can announce.
            //
            // Asserting on <h1> says that directly, and survives the next
            // redesign without an edit.
            Assert.True(
                page.Contains("<h1", StringComparison.Ordinal),
                $"{route} needs a visible <h1> entry point for browser QA.");
        }
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Concierge.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate Concierge.slnx from test output directory.");
    }
}
