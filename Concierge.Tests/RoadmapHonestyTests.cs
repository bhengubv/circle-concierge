using Bunit;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The Roadmap room, which used to say all the work was done.
///
/// **Fifteen pieces of hardening work, every one of them marked "Ready", because
/// `ConciergeStateService.Task(...)` writes `HardeningStatus.Ready` into every one of them as
/// a literal.** Not a measurement, not derived from anything: a constant, printed on the one
/// screen whose job is to say where things stand.
///
/// It said "File sandbox hardening — Ready" on a day macOS, Android and iOS are unconfined
/// and Windows has no route to cap what a command writes. It said "Performance and resource
/// tests — Ready" beside fourteen other things nobody had measured.
///
/// This is the approvals badge that always said two, in the room where it costs the most, and
/// this file is what stops it coming back.
/// </summary>
public sealed class RoadmapHonestyTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Pages.Roadmap> Room()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<Concierge.Shared.Components.Pages.Roadmap>();
    }

    /// <summary>
    /// A list of work in order does not need a status beside it, and a status nobody derives
    /// is worse than none.
    /// </summary>
    [Fact]
    public void It_does_not_tell_anybody_the_work_is_finished()
    {
        var room = Room();

        Assert.DoesNotContain("Ready", room.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void And_says_plainly_that_it_is_an_order_rather_than_a_progress_board()
        => Assert.Contains("not a progress board", Room().Markup, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// In words, not in markdown. The first version of the sentence above was written with
    /// asterisks round the important half, and the screen printed the asterisks — a room is
    /// Razor, not a README. Caught by looking at it, which is the only thing that catches it.
    /// </summary>
    [Fact]
    public void And_says_it_in_words_rather_than_in_markdown()
        => Assert.DoesNotContain("**", Room().Markup, StringComparison.Ordinal);

    /// <summary>
    /// The work itself still shows: the order is the content of this room, and removing a
    /// false status must not quietly remove the list with it.
    /// </summary>
    [Fact]
    public void The_work_and_its_order_are_still_there()
    {
        var room = Room();

        Assert.Contains("File sandbox hardening", room.Markup, StringComparison.Ordinal);
        Assert.Contains("Real Git and release support", room.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where a status is real, it is kept. Source control asks the machine whether there is a
    /// repository and answers Blocked when there is not — that one is a measurement, and this
    /// test is here so nobody reads the change above as "statuses are bad".
    /// </summary>
    [Fact]
    public void A_status_that_is_measured_is_still_a_status()
    {
        var gates = new ConciergeStateService().GetSnapshot().ProductionGates;

        Assert.Contains(gates, gate => gate.Status != HardeningStatus.Ready);
    }
}
