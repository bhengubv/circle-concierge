using Bunit;
using CircleAI.ContentPolicy;
using Concierge.Shared;
using Concierge.Shared.Safety;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// What the filter caught, and what happens when nobody can tell.
///
/// **Found by following a cross-reference.** "Who is chatting" sends a parent to Settings
/// under Family mode for "what the filter has caught", and Family mode showed a switch and
/// three strictness levels and nothing else. The section does exist — correctly hidden while
/// there is nothing to show — which is worth saying because the first version of this fix
/// rewrote that room to say no such record was kept, and that would have been a false
/// sentence shipped to fix a true one.
///
/// The real defect was underneath: `RefreshAuditAsync` caught **every** failure and left the
/// list empty, and the section is hidden when the list is empty. So an audit file that was
/// corrupt, locked, or on a drive that had gone away produced a clean panel — identical, to
/// the pixel, to a filter that had stopped nothing.
///
/// That is this product's signature defect at its worst: two different facts rendering the
/// same, on the one screen a parent opens to find out which of them is true.
/// </summary>
public sealed class FilterRecordTests : BunitContext
{
    /// <summary>A log that cannot be read — a drive that has gone away, a locked file.</summary>
    private sealed class Unreadable : ISafetyAuditLog
    {
        public string BackendId => "unreadable";

        public ValueTask LogAsync(SafetyAuditEntry entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<SafetyAuditEntry>> ReadAsync(
            string? userId, int limit = 100, CancellationToken ct = default)
            => throw new IOException("The device is not ready.");
    }

    /// <summary>A log that can be read and holds nothing.</summary>
    private sealed class Empty : ISafetyAuditLog
    {
        public string BackendId => "empty";

        public ValueTask LogAsync(SafetyAuditEntry entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<SafetyAuditEntry>> ReadAsync(
            string? userId, int limit = 100, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SafetyAuditEntry>>([]);
    }

    private IRenderedComponent<Concierge.Shared.Components.Pages.Settings> Panel(ISafetyAuditLog log)
    {
        // Registered before AddConciergeCore, which uses TryAddSingleton for the audit log —
        // so first registration wins and the real JSONL one stands down.
        Services.AddSingleton(log);
        Services.AddLogging();
        Services.AddConciergeCore();
        Concierge.Shared.Safety.SafetyServiceCollectionExtensions.AddConciergeSafety(Services);
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Concierge.Shared.Components.Pages.Settings>();
    }

    /// <summary>
    /// A log nobody can read says so, rather than showing the same clean panel a quiet
    /// afternoon shows.
    /// </summary>
    [Fact]
    public void A_record_that_cannot_be_read_is_not_reported_as_an_empty_one()
    {
        var panel = Panel(new Unreadable());

        // The panel is an accordion with "Who answers" open, so the group has to be unfolded
        // before its body exists. That the group is *there at all* is the first half of the
        // finding, so it is asserted before it is opened.
        var head = panel.FindAll("button.sheet-head-btn")
            .Single(button => button.TextContent.Contains("What the filter caught", StringComparison.Ordinal));

        head.Click();

        Assert.Contains("The record could not be read", panel.Markup, StringComparison.Ordinal);
        Assert.Contains("The device is not ready", panel.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a filter that has genuinely caught nothing still says nothing — the section stays
    /// hidden. Fixing the first case must not turn every quiet day into a warning.
    /// </summary>
    [Fact]
    public void And_a_record_that_is_simply_empty_still_shows_nothing()
    {
        var panel = Panel(new Empty());

        Assert.DoesNotContain("What the filter caught", panel.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", panel.Markup, StringComparison.Ordinal);
    }
}
