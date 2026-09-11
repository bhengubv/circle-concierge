using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// The run log, which grew forever and was rewritten whole every time.
///
/// **Found by opening Engineering and reading the number.** "Recent runs — 669", going back
/// to the first of September, in a single `runs.json` that is serialised and written out in
/// full on every run. So the cost of running one command grew with the number of commands
/// ever run: at 669 runs, every `read_file` rewrote 669 records to disk before it answered.
/// Each record carries the whole output of every tool call in it, so one read of a large file
/// sits in there permanently and is copied again by every run that follows.
///
/// Nothing reads further back than the eight rows the room shows.
///
/// And it was written with `File.WriteAllText` straight over the top. `DesignStore` writes
/// beside and moves into place for exactly this reason — *"losing the last change is
/// recoverable; losing the design is not"* — and this file never got the lesson. A write
/// interrupted by the app closing leaves truncated JSON, `LoadLogs` catches the parse failure
/// and returns an empty list, and the whole history is gone without a word.
/// </summary>
public sealed class RunLogGrowthTests
{
    private static AgentHarnessService In(string root) => new(root);

    private static string AWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"runlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// The log stops growing. Two hundred and twenty runs, two hundred kept.
    /// </summary>
    [Fact]
    public async Task The_log_keeps_a_bounded_number_of_runs()
    {
        var root = AWorkspace();
        var harness = In(root);

        for (var i = 0; i < 220; i++)
        {
            await harness.RunGoalAsync($"run {i}", ["dotnet --info && dotnet build"], approved: true);
        }

        Assert.Equal(200, harness.GetRunLogs().Count);
    }

    /// <summary>
    /// And it keeps the newest, not the first two hundred it happened to see. Dropping the
    /// recent end would leave a room called "Recent runs" showing September.
    /// </summary>
    [Fact]
    public async Task And_keeps_the_newest_rather_than_the_oldest()
    {
        var root = AWorkspace();
        var harness = In(root);

        for (var i = 0; i < 210; i++)
        {
            await harness.RunGoalAsync($"run {i}", ["dotnet --info && dotnet build"], approved: true);
        }

        var goals = harness.GetRunLogs().Select(log => log.Goal).ToList();

        Assert.Contains("run 209", goals);
        Assert.DoesNotContain("run 0", goals);
    }

    /// <summary>
    /// Nothing is left behind beside the log. A staging file that survives is a file that
    /// gets committed, backed up, or mistaken for the real one.
    /// </summary>
    [Fact]
    public async Task And_leaves_no_half_written_file_beside_it()
    {
        var root = AWorkspace();

        await In(root).RunGoalAsync("one run", ["dotnet --info && dotnet build"], approved: true);

        var directory = Path.Combine(root, ".concierge-artifacts", "agent-runs");

        Assert.True(File.Exists(Path.Combine(directory, "runs.json")));
        Assert.Empty(Directory.GetFiles(directory, "*.writing"));
    }

    /// <summary>
    /// A later harness reads back what an earlier one wrote. The point of bounding the file
    /// is not to make it disposable.
    /// </summary>
    [Fact]
    public async Task And_the_runs_that_are_kept_survive_a_restart()
    {
        var root = AWorkspace();

        await In(root).RunGoalAsync("before", ["dotnet --info && dotnet build"], approved: true);

        Assert.Contains(In(root).GetRunLogs(), log => log.Goal == "before");
    }
}
