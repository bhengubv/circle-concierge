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
        }
        catch
        {
            _auditEntries = Array.Empty<SafetyAuditEntry>();
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
