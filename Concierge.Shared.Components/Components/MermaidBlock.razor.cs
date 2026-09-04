// Code-behind for MermaidBlock. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Components;

public partial class MermaidBlock
{

    [Parameter, EditorRequired] public string Source { get; set; } = string.Empty;

    private readonly string _hostId = $"mermaid-host-{Guid.NewGuid():N}";
    private readonly string _renderId = $"mermaid-render-{Guid.NewGuid():N}";
    private IJSObjectReference? _module;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // mermaid.js is loaded lazily — the import only fires the first time a chat reply
        // actually contains a diagram, so the initial circuit handshake stays small.
        if (string.IsNullOrWhiteSpace(Source))
        {
            return;
        }

        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Concierge.Shared.Components/concierge-mermaid.js");
            await _module.InvokeVoidAsync("renderInto", _hostId, _renderId, Source);
        }
        catch (JSDisconnectedException)
        {
            // The Blazor Server circuit went away (page change, network blip). Nothing to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit closed — the module reference is already gone.
            }
        }
    }
}
