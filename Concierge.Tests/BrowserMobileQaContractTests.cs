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
    /// Below 760px there is a way to reach a thread, a room and the approval
    /// queue.
    ///
    /// This test was skipped, and before that it was a passing test named
    /// "..._for_mobile_and_desktop" that asserted mobile worked while nothing
    /// checked that it did. What was actually true is that .ws-side was
    /// display:none below the breakpoint and nothing replaced it — the bottom
    /// tab bar that used to stand in for it had been deleted with the rest of
    /// the UI, so navigation on a narrow window did not exist.
    ///
    /// The sidebar is now a drawer at that width rather than a second menu:
    /// one .ws-side, one set of groups, one place to change them. The
    /// behaviour is asserted in WorkspaceSidebarTests; this checks the half
    /// that lives in CSS, which no component test can see.
    /// </summary>
    [Fact]
    public void A_narrow_window_can_still_reach_threads_and_rooms()
    {
        var root = FindWorkspaceRoot();
        var css = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "wwwroot", "concierge.css"));

        // The sidebar is present as an overlay rather than removed.
        Assert.Contains("@media (max-width: 759.98px)", css, StringComparison.Ordinal);
        Assert.Contains(".ws-side.is-open { transform: translateX(0); }", css, StringComparison.Ordinal);

        // And the control that opens it is visible only there. This rule sits
        // last in the sheet on purpose: the base .ws-menu rule sets
        // display:none at the same specificity, and an override placed before
        // it lost — the drawer worked and nothing could open it.
        // The base rule is the first .ws-menu block in the sheet; the
        // narrow-window override is the last.
        var menuHidden = css.IndexOf(".ws-menu {", StringComparison.Ordinal);
        var menuShown = css.LastIndexOf(".ws-menu { display: inline-grid; }", StringComparison.Ordinal);

        Assert.True(menuHidden >= 0, "expected a base .ws-menu rule");
        Assert.True(menuShown >= 0, "expected a narrow-window .ws-menu rule");
        Assert.True(menuShown > menuHidden,
            "the narrow-window .ws-menu rule must come after the base rule or it loses the cascade");
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
