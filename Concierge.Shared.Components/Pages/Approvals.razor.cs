// Code-behind for Approvals. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Pages;

public partial class Approvals
{

    // ── Risk, read four ways ──────────────────────────────────────────────
    // Risk arrives as a free string. Every one of these falls through to the
    // calmest reading rather than throwing or showing a colour it cannot
    // justify — an unrecognised risk is not an emergency.

    private static int RiskWeight(string? risk) => (risk ?? "").ToLowerInvariant() switch
    {
        "critical" => 4,
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    private static string RiskPill(string? risk) => (risk ?? "").ToLowerInvariant() switch
    {
        "high" or "critical" => "pill-danger",
        "medium" => "pill-waiting",
        _ => "pill-queued",
    };

    private static string RiskDot(string? risk) => (risk ?? "").ToLowerInvariant() switch
    {
        "high" or "critical" => "dot-failed",
        "medium" => "dot-waiting",
        _ => "dot-queued",
    };

    /// <summary>
    /// What a risk level actually means in reach. Derived from the level rather
    /// than written per item, because the queue carries no scope field and a
    /// specific-sounding sentence that is not backed by data is worse than a
    /// general one that is true.
    /// </summary>
    private static string ReachOf(string? risk) => (risk ?? "").ToLowerInvariant() switch
    {
        "high" or "critical" => "It can reach files and tools outside this conversation",
        "medium" => "It can change things inside this workspace",
        _ => "It stays inside this conversation",
    };


    // In-memory verdicts keyed by approval id. Persistence is a layered concern
    // (IApprovalSink etc.); for the v1 dashboard the user gets immediate visual
    // feedback + an audit list. Server-side persistence wires in by replacing
    // this dictionary with a service-backed store.
    private readonly Dictionary<string, string> _decided = new();
    // Optional caller-injected override for the queue (used by tests). Real
    // runtime always falls back to ConciergeStateService.GetSnapshot().Approvals.
    private List<ApprovalRequest>? _local = null;

    private void Approve(ApprovalRequest approval)
    {
        _decided[approval.Id] = "approved";
        Snackbar.Add($"Approved · {approval.Title}", Severity.Success);
    }

    private void Reject(ApprovalRequest approval)
    {
        _decided[approval.Id] = "rejected";
        Snackbar.Add($"Rejected · {approval.Title}", Severity.Warning);
    }

    private static string RiskClass(string? risk) => (risk ?? "").ToLowerInvariant() switch
    {
        "high" or "critical" => "risk-high",
        "medium" => "risk-medium",
        _ => "risk-low",
    };

    private static string RelativeTime(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)   return $"{(int)span.TotalHours}h ago";
        return when.ToLocalTime().ToString("MMM d, HH:mm");
    }
}
