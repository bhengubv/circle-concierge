// Code-behind for Bell. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Components;

public partial class Bell
{

    /// <summary>"idle" | "listening" | "thinking" | "cheering".</summary>
    [Parameter] public string State { get; set; } = "idle";
}
