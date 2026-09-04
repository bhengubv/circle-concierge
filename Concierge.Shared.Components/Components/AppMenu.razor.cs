// Code-behind for AppMenu. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Components;

public partial class AppMenu
{

    private bool _open;

    protected override void OnInitialized()
    {
        Nav.LocationChanged += OnNavigated;
    }

    public void Dispose() => Nav.LocationChanged -= OnNavigated;

    private void OnNavigated(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        if (_open)
        {
            _open = false;
            InvokeAsync(StateHasChanged);
        }
    }

    private void Toggle() => _open = !_open;
    private void Close() => _open = false;

    private sealed record Item(string Title, string Sub, string Href, string IconSvg);

    /// <summary>
    /// The overflow inventory. Home, Chat, History, Skills and Approvals are
    /// tabs in ShellLayout and deliberately do not appear here — a destination
    /// in two places is a destination you have to check twice.
    /// </summary>
    private readonly Item[] _items =
    {
        new("Settings", "Models, keys, limits, family mode",
            "/settings",
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 0 1-4 0v-.1a1.7 1.7 0 0 0-1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1A2 2 0 1 1 4.4 17l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 0 1 0-4h.1a1.7 1.7 0 0 0 1.5-1 1.7 1.7 0 0 0-.3-1.8l-.1-.1A2 2 0 1 1 7 4.4l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 0 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1A2 2 0 1 1 19.6 7l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 0 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>"""),

        new("Switch mode", "Adult, kid, or both",
            "/switch-mode",
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="8"  cy="12" r="5"/><circle cx="16" cy="12" r="5"/></svg>"""),

        new("Help", "How this works",
            "/help",
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M9.1 9a3 3 0 1 1 5.8 1c0 2-3 2-3 4"/><circle cx="12" cy="17" r="0.6" fill="currentColor"/></svg>"""),

        new("About", "What this is and who made it",
            "/about",
            """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><line x1="12" y1="11" x2="12" y2="17"/><circle cx="12" cy="8" r="0.6" fill="currentColor"/></svg>"""),
    };
}
