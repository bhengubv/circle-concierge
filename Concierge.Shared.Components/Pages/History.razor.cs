// Code-behind for History. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Concierge.Shared.Chat;

namespace Concierge.Shared.Components.Pages;

public partial class History
{

    private string _query = string.Empty;

    // Narrow while typing. Waiting for blur on a list that only grows is
    // waiting for the wrong event.
    private void OnQueryChanged(ChangeEventArgs e)
        => _query = e.Value?.ToString() ?? string.Empty;

    // "Today" and "Yesterday" are what people actually say; past that a date is
    // more useful than a count of days.
    private static string DayName(DateTime day)
    {
        var today = DateTime.Now.Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year
            ? day.ToString("dddd d MMMM")
            : day.ToString("d MMMM yyyy");
    }


    private const string OwnerId = "local";
    private bool _loading = true;
    private List<Conversation>? _conversations;
    private Guid? _pendingDeleteId;

    protected override async Task OnInitializedAsync()
    {
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            var list = await Store.ListAsync(OwnerId);
            _conversations = list.OrderByDescending(c => c.UpdatedAt).ToList();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RequestDelete(Guid id) => _pendingDeleteId = id;
    private void CancelDelete() => _pendingDeleteId = null;

    private async Task ConfirmDelete()
    {
        var id = _pendingDeleteId;
        _pendingDeleteId = null;
        if (id is null) return;
        try { await JS.InvokeVoidAsync("conciergeSense.hapticAttention"); } catch { }
        await Store.DeleteAsync(id.Value);
        await ReloadAsync();
    }

    private static string RelativeTime(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24)   return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7)     return $"{(int)span.TotalDays} d ago";
        return when.ToLocalTime().ToString("MMM d, HH:mm");
    }
}
