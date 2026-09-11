using System.Runtime.InteropServices;
using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// A command that finishes before anybody looks at it.
///
/// **Found by a test failing about one full-suite run in three**, with a message that named
/// the product rather than the test: `System.ArgumentException : Process with an Id of 9380 is
/// not running`, thrown out of `ConfinedProcess.RunAsync`.
///
/// The sandbox created the child with `CreateProcess`, held its handle — and then ignored the
/// handle and looked the process up again by its id, with
/// `Process.GetProcessById(info.dwProcessId)`. That call throws when the process has already
/// exited, and `cmd /c echo hello` exits well inside the time it takes to get there. So a
/// command that ran perfectly came back as an exception out of the sandbox, and how often
/// depended entirely on how busy the machine was. The handle cannot race: it stays valid
/// after the child exits and is signalled the moment the child ends.
///
/// **The honest limit on the first test below: it does not reproduce the race, and it passes
/// against the broken version.** That was measured, not assumed — the old line was put back
/// and the test run against it: thirty in sequence, thirty at once and two hundred at once
/// all passed. Losing that race needs this thread descheduled between `CreateProcess` and the
/// lookup, and nothing here can arrange that on demand. Reporting it as the proof would be
/// the thing this repository keeps catching itself doing, so it is written down instead.
///
/// What the first test does prove is that fast commands work, which is the behaviour that
/// broke. What stops the defect coming back is the second one: the lookup is gone, and a
/// source check goes red if anybody reintroduces it. Crude, and it is what is available — the
/// same trade the 3D engine's ordering tests make.
/// </summary>
public sealed class FastCommandTests
{
    private static AgentHarnessService Harness()
        => new(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")));

    /// <summary>
    /// Thirty of the fastest command there is. One is not a test of a race — the whole point
    /// is that the old code passed most of the time.
    /// </summary>
    [Fact]
    public async Task A_command_too_fast_to_catch_still_comes_back_with_its_output()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var harness = Harness();

        // Run together rather than one after another, which loads the machine the way the
        // full suite did when it caught this. It is not enough to force the race — see the
        // note on the class — but a fast command under load is the shape that broke.
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ =>
            harness.RunCommandAsync("cmd /c echo hello", approved: true)));

        Assert.All(results, result =>
        {
            Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
            Assert.Contains("hello", result.Output, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// And the exit code is still the child's own, which is the thing the handle is read for.
    /// A command that fails must not come back looking like one that worked.
    /// </summary>
    [Fact]
    public async Task And_a_fast_failure_is_still_a_failure()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var result = await Harness().RunCommandAsync("cmd /c exit 3", approved: true);

        Assert.Equal(3, result.ExitCode);
        Assert.NotEqual(ConciergeToolOutcome.Succeeded, result.Outcome);
    }

    /// <summary>
    /// The tripwire. `Process.GetProcessById` after `CreateProcess` is the defect itself:
    /// it throws away a handle that cannot race in favour of a lookup that can, and it will
    /// look like perfectly reasonable code to whoever writes it next.
    /// </summary>
    [Fact]
    public void And_the_sandbox_never_looks_a_process_up_by_its_id_again()
    {
        var lines = File.ReadAllLines(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Concierge.Shared", "Sandboxing", "ConfinedProcess.cs")));

        // Comment lines dropped first, because the paragraph above this test explains the
        // defect by name and would otherwise match itself.
        var code = string.Join(
            Environment.NewLine,
            lines.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("GetProcessById", code, StringComparison.Ordinal);
    }
}
