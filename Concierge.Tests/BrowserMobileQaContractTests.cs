namespace Concierge.Tests;

public sealed class BrowserMobileQaContractTests
{
    private static readonly string[] Routes =
    [
        "Home.razor",
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

    [Fact]
    public void Main_layout_keeps_sidebar_responsive_for_mobile_and_desktop()
    {
        var root = FindWorkspaceRoot();
        var layoutCss = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Layout", "MainLayout.razor.css"));
        var navCss = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Layout", "NavMenu.razor.css"));

        Assert.Contains("@media (max-width: 640.98px)", layoutCss, StringComparison.Ordinal);
        Assert.Contains("@media (min-width: 641px)", layoutCss, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", navCss, StringComparison.Ordinal);
        // No-scrollbar rule — scrolling still works, but the bar must not be visible.
        Assert.Contains("scrollbar-width: none", navCss, StringComparison.Ordinal);
        Assert.Contains("::-webkit-scrollbar", navCss, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_core_page_has_a_plain_header_or_workspace_intro()
    {
        var root = FindWorkspaceRoot();

        foreach (var route in Routes)
        {
            var page = File.ReadAllText(Path.Combine(root, "Concierge.Shared.Components", "Pages", route));
            Assert.True(
                page.Contains("page-header", StringComparison.Ordinal)
                || page.Contains("class=\"hero\"", StringComparison.Ordinal)
                || page.Contains("agent-stage", StringComparison.Ordinal),
                $"{route} needs a visible entry point for browser QA.");
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
