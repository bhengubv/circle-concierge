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
    /// Where a status is real, it is kept — so nobody reads the change above as "statuses are
    /// bad". The example this test cited was wrong, and correcting it is the point.
    ///
    /// It said source control "asks the machine whether there is a repository and answers
    /// Blocked when there is not — that one is a measurement". It was not. It was a second
    /// literal, sitting two lines below the fifteen this file was written about, and it
    /// passed this test for the same reason the fifteen would have: the assertion only ever
    /// checked that *something* was not Ready, which a typed word satisfies perfectly.
    ///
    /// It is a measurement now (see <c>ReleaseGateTests</c>), so the example is real and the
    /// assertion is the one that could tell the difference: the gate gives a different answer
    /// in a folder with a repository and a folder without one.
    /// </summary>
    [Fact]
    public void A_status_that_is_measured_is_still_a_status()
    {
        var withGit = Path.Combine(Path.GetTempPath(), $"roadmap-{Guid.NewGuid():N}");
        var without = Path.Combine(Path.GetTempPath(), $"roadmap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(withGit, ".git"));
        Directory.CreateDirectory(without);

        static ProductionGate Gate(string root)
            => new ConciergeStateService(new SkillCatalogService(), new SourceControlService(), root)
                .GetSnapshot()
                .ProductionGates
                .Single(gate => gate.Id == "source-control");

        Assert.NotEqual(Gate(withGit).Status, Gate(without).Status);
    }
}
