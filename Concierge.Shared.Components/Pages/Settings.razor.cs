// Code-behind for Settings. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using CircleAI.ContentPolicy;
using Concierge.Shared.Safety;

namespace Concierge.Shared.Components.Pages;

public partial class Settings
{

    private const string ProviderPreferenceStorageKey = "concierge-provider-id";
    private string? _selectedProviderId;
    private IReadOnlyList<SafetyAuditEntry> _auditEntries = Array.Empty<SafetyAuditEntry>();

    /// <summary>
    /// Set when the log could not be read, which is not the same as nothing having been
    /// caught — and until this existed the two looked identical. The section is hidden when
    /// there are no entries, correctly; a `catch` that swallowed the failure and left the
    /// list empty hid it in exactly the same way. So a parent whose audit file was corrupt,
    /// locked, or on a drive that had gone away saw a clean panel and concluded the filter
    /// had stopped nothing.
    ///
    /// The worst version of this product's signature defect: two different facts rendering
    /// the same, on the screen a parent opens precisely to find out which one is true.
    /// </summary>
    private string? _auditUnreadable;

    private async Task OnKidModeChanged(ChangeEventArgs args)
    {
        SafetySettings.KidMode = args.Value is bool b && b;
        // Kid mode bumps Strictness to Strict unless the parent has already
        // moved it to Balanced (older child).
        if (SafetySettings.KidMode && SafetySettings.Strictness == SafetyStrictness.Off)
        {
            SafetySettings.Strictness = SafetyStrictness.Strict;
        }
        await RefreshAuditAsync();
    }

    private async Task OnStrictnessChanged(ChangeEventArgs args)
    {
        if (Enum.TryParse<SafetyStrictness>(args.Value?.ToString(), out var s))
        {
            SafetySettings.Strictness = s;
        }
        await RefreshAuditAsync();
    }

    private void OnProfileLabelChanged(ChangeEventArgs args)
    {
        SafetySettings.ProfileLabel = args.Value?.ToString();
    }

    private async Task RefreshAuditAsync()
    {
        try
        {
            _auditEntries = await SafetyAudit.ReadAsync(null, limit: 20);
            _auditUnreadable = null;
        }
        catch (Exception failure)
        {
            _auditEntries = Array.Empty<SafetyAuditEntry>();
            _auditUnreadable = failure.Message;
        }

        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            _selectedProviderId = await JS.InvokeAsync<string?>("localStorage.getItem", ProviderPreferenceStorageKey);
        }
        catch { /* localStorage unavailable during prerender or on platforms that block it. */ }
        await RefreshAuditAsync();
    }

    private async Task SelectProviderAsync(string providerId)
    {
        _selectedProviderId = providerId;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", ProviderPreferenceStorageKey, providerId);
        }
        catch { /* best-effort */ }
        StateHasChanged();
    }
}
