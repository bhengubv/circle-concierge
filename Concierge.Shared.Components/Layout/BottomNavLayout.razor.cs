// Code-behind for BottomNavLayout. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Layout;

public partial class BottomNavLayout
{

    private string _currentPath = "/";
    private List<Tab> _tabs = new();

    protected override void OnInitialized()
    {
        Nav.LocationChanged += OnLocationChanged;
        RefreshState();
    }

    protected override void OnParametersSet() => RefreshState();

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        RefreshState();
        InvokeAsync(StateHasChanged);
    }

    private void RefreshState()
    {
        _currentPath = "/" + Nav.ToBaseRelativePath(Nav.Uri).TrimEnd('/');
        if (_currentPath == "/") { /* normalise */ }

        var pendingApprovals = 0;
        try
        {
            // Approvals count drives the badge on the Approvals tab. Falling
            // back to 0 on any service failure so the shell never crashes the
            // app over a stat lookup. ApprovalRequest is the public record.
            pendingApprovals = State.GetSnapshot()?.Approvals.Count ?? 0;
        }
        catch { /* shell never throws over a stat lookup */ }

        _tabs = new List<Tab>
        {
            new("/",          "Today",     IconHome,      0),
            new("/chat",      "Chat",      IconChat,      0),
            new("/skills",    "Skills",    IconSkills,    0),
            new("/approvals", "Approvals", IconApprovals, pendingApprovals),
        };
    }

    private bool IsTabActive(Tab t)
    {
        if (t.Href == "/")
        {
            return _currentPath == "/" || _currentPath == string.Empty;
        }
        return _currentPath.StartsWith(t.Href, StringComparison.OrdinalIgnoreCase);
    }

    private void GoSettings() => Nav.NavigateTo("/settings");

    private static string TopTitleForPath(string path) => path.TrimStart('/').ToLowerInvariant() switch
    {
        ""           => "Today",
        "chat"       => "Chat",
        var p when p.StartsWith("chat/") => "Chat",
        "skills"     => "Skills",
        "approvals"  => "Approvals",
        "settings"   => "Settings",
        "images"     => "Images",
        "diagrams"   => "Diagrams",
        "release"    => "Release",
        "roadmap"    => "Roadmap",
        "engineering"=> "Build Room",
        "product"    => "Product Room",
        "beyond"     => "Beyond Code",
        "business-apis" => "Business APIs",
        "pricing"    => "Pricing",
        _            => "Concierge",
    };

    public void Dispose() => Nav.LocationChanged -= OnLocationChanged;

    private sealed record Tab(string Href, string Label, string IconSvg, int BadgeCount);

    // ── Custom SVG icons. Inline so no extra package, no font fallback risk,
    //    no FOUC. Lucide-style stroke (1.75px, round caps) — thin and geometric
    //    instead of Material's chunky fills. ──────────────────────────────────

    private const string IconHome =
        """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12l9-8 9 8" /><path d="M5 10v10h14V10" /><path d="M9 20v-6h6v6" /></svg>""";

    private const string IconChat =
        """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5h16v11H8l-4 4z" /></svg>""";

    private const string IconSkills =
        """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="9" r="3" /><path d="M5 21c0-4 3-7 7-7s7 3 7 7" /><path d="M15.5 5.5l1.5-1.5M8.5 5.5L7 4M18 9h2M4 9h2" /></svg>""";

    private const string IconApprovals =
        """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M5 4h11l4 4v12H5z" /><path d="M9 12l2 2 4-4" /></svg>""";
}
