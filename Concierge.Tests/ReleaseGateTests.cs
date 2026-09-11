using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// The Release gates, which used to be four sentences somebody typed.
///
/// **Found by opening the Release room and reading it.** It said "Passing 2 of 4" over four
/// gates, and under them: *"Each line names how the gate was checked, so a claim here can be
/// repeated by somebody who does not trust it."* Not one of the four had been checked. Two
/// said `Ready` because the word `Ready` was written in a list. One said **"No .git repository
/// detected by the app yet"** in a repository with a full history — and went on saying it
/// after a commit and a push, because nothing was detecting anything.
///
/// `SourceControlService` had been sitting in the tree the whole time, registered in the
/// container, performing exactly that check properly, and **resolved by nothing outside its
/// own tests**. Tenth instance of this repository's signature defect, on the one screen whose
/// entire job is saying whether a build may ship.
///
/// A correction of mine belongs here too: a test written earlier this week said the
/// source-control gate "asks the machine whether there is a repository", and cited it as the
/// example of a status that was genuinely measured. It was a second literal. The claim was
/// wrong when it was written and it is true now.
/// </summary>
public sealed class ReleaseGateTests
{
    private static ProductionGate GateIn(string root, string id)
        => new ConciergeStateService(new SkillCatalogService(), new SourceControlService(), root)
            .GetSnapshot()
            .ProductionGates
            .Single(gate => gate.Id == id);

    private static string AFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Both answers, which is the whole point: a gate that can only ever say one thing is a
    /// sentence, not a gate, and nothing about the old one could be tested because nothing
    /// about it could change.
    /// </summary>
    [Fact]
    public void Source_control_says_yes_where_there_is_a_repository()
    {
        var root = AFolder();
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        Assert.Equal(HardeningStatus.Ready, GateIn(root, "source-control").Status);
    }

    [Fact]
    public void And_says_no_where_there_is_not()
        => Assert.Equal(HardeningStatus.Blocked, GateIn(AFolder(), "source-control").Status);

    /// <summary>
    /// And it names where it looked. "No .git repository detected" is unarguable-with;
    /// "no .git found, looked in C:\…\win-x64" is something a person can disagree with,
    /// which is the difference between evidence and an assertion.
    /// </summary>
    [Fact]
    public void And_names_the_folder_it_looked_in()
    {
        var root = AFolder();

        Assert.Contains(root, GateIn(root, "source-control").Evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a build compiles and whether a suite passes are facts about a build machine.
    /// An app cannot learn them by looking at itself, so it does not claim to.
    /// </summary>
    [Theory]
    [InlineData("build-tests")]
    [InlineData("agent-harness")]
    public void What_nothing_measures_is_marked_not_checked_rather_than_ready(string id)
        => Assert.Equal(HardeningStatus.NotChecked, GateIn(AFolder(), id).Status);

    /// <summary>
    /// Not checked is not passing. The room counts the ready ones, so a gate that borrows the
    /// word Ready inflates the number above it — which is how "Passing 2 of 4" was produced
    /// out of nothing at all.
    /// </summary>
    [Fact]
    public void And_an_unchecked_gate_is_not_counted_as_passing()
    {
        var gates = new ConciergeStateService(
                new SkillCatalogService(), new SourceControlService(), AFolder())
            .GetSnapshot()
            .ProductionGates;

        Assert.DoesNotContain(
            gates.Where(gate => gate.Status == HardeningStatus.NotChecked),
            gate => gate.Status == HardeningStatus.Ready);

        Assert.Equal(0, gates.Count(gate => gate.Status == HardeningStatus.Ready));
    }
}
