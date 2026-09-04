// Code-behind for ApprovalPrompt. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Components.Components;

public partial class ApprovalPrompt
{

    private InteractiveToolApprovalService? _approver;
    private IReadOnlyList<PendingApproval> _pending = [];

    protected override void OnInitialized()
    {
        // The injected service may be the fail-closed default, in which case there is nothing
        // to show and nothing to subscribe to.
        _approver = Approval as InteractiveToolApprovalService;
        if (_approver is null)
        {
            return;
        }

        _approver.PendingChanged += OnPendingChanged;
        _pending = _approver.Pending;
    }

    private void OnPendingChanged(object? sender, EventArgs e)
    {
        _pending = _approver!.Pending;

        // Raised from whichever thread ran the tool call, so the update has to be marshalled
        // back onto the renderer's.
        _ = InvokeAsync(StateHasChanged);
    }

    private void Answer(Guid id, ToolApprovalDecision decision)
    {
        _approver?.Answer(id, decision);
        _pending = _approver?.Pending ?? [];
    }

    public void Dispose()
    {
        if (_approver is not null)
        {
            _approver.PendingChanged -= OnPendingChanged;
        }
    }
}
