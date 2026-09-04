// Code-behind for Beyond. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Pages;

public partial class Beyond
{

    /// <summary>Whether every capability is showing. Starts false so the room
    /// fits the window it opens in.</summary>
    private bool _allCapabilities;

    private static string YesNo(bool value)
    {
        return value ? "yes" : "no";
    }
}
