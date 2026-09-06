using System.Diagnostics;
using Concierge.Shared;
using Concierge.Shared.Sandboxing;

namespace Concierge.Tests;

/// <summary>
/// A boundary around a command.
///
/// Concierge.Shared.Sandboxing held a complete Windows job-object sandbox —
/// memory cap, process cap, and KILL_ON_JOB_CLOSE — referenced nowhere, while
/// the one method in the product that starts a process started it with nothing
/// at all. The Engineering room says "what Concierge can do to your machine"
/// above a list that includes run_command, and the answer was: whatever it likes.
///
/// The guarantee that matters most is the one killing the process tree on timeout
/// never covered: a command that starts something in the background and exits
/// cleanly was never timed out, so nothing ever killed what it left behind. That
/// is the failure nobody finds until a phone is warm in a pocket.
/// </summary>
public sealed class SandboxedCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-sandbox-tests", Guid.NewGuid().ToString("N"));

    public SandboxedCommandTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Concierge.slnx"), string.Empty);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    private AgentHarnessService Harness() => new(_root);

    // ── What this platform can enforce ────────────────────────────────────

    /// <summary>
    /// Said out loud, so a caller can decline rather than assume. The honest
    /// answer on a platform that cannot confine anything is "none", and Engineering
    /// reports whichever it is.
    /// </summary>
    [Fact]
    public void The_harness_says_what_confines_a_command_here()
    {
        var confinement = Harness().Confinement;

        Assert.False(string.IsNullOrWhiteSpace(confinement.Mechanism));
        Assert.False(string.IsNullOrWhiteSpace(confinement.Explanation));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(SandboxStrength.Process, confinement.Strength);
            Assert.Equal("windows-job-object", confinement.Mechanism);
        }
    }

    /// <summary>
    /// A job object limits and terminates; it does not restrict which files a
    /// process may open. Claiming Confined would be a stronger promise than
    /// Windows actually makes.
    /// </summary>
    [Fact]
    public void It_does_not_claim_to_confine_more_than_it_does()
    {
        Assert.NotEqual(SandboxStrength.Confined, Harness().Confinement.Strength);
    }

    // ── Still doing its job ───────────────────────────────────────────────

    /// <summary>
    /// The confinement must not cost the output — the output of a command is the
    /// entire reason for running it, and Prepare sets UseShellExecute, which is
    /// exactly the property that silently disables redirection.
    /// </summary>
    [Fact]
    public async Task A_confined_command_still_returns_what_it_printed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await Harness().RunCommandAsync("cmd /c echo confined", approved: true);

        Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
        Assert.Contains("confined", result.Output);
    }

    [Fact]
    public async Task A_failing_command_still_reports_its_exit_code()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await Harness().RunCommandAsync("cmd /c exit 3", approved: true);

        Assert.Equal(ConciergeToolOutcome.Failed, result.Outcome);
        Assert.Contains("3", result.Summary);
    }

    /// <summary>
    /// Confinement happens after the allowlist and the approval, not instead of
    /// them. A command that was never going to run must not be run in a box.
    ///
    /// A destructive one, deliberately: the harness runs read-only commands
    /// unattended on purpose, so "git status" without approval is allowed to run
    /// and proves nothing about approval.
    /// </summary>
    [Fact]
    public async Task An_unapproved_destructive_command_is_still_refused_before_anything_starts()
    {
        var result = await Harness().RunCommandAsync("git reset --hard", approved: false);

        Assert.Equal(ConciergeToolOutcome.ApprovalRequired, result.Outcome);
    }

    [Fact]
    public async Task A_command_that_is_not_allowlisted_is_still_refused()
    {
        var result = await Harness().RunCommandAsync("curl https://example.com", approved: true);

        Assert.NotEqual(ConciergeToolOutcome.Succeeded, result.Outcome);
    }

    // ── What the boundary is for ──────────────────────────────────────────

    /// <summary>
    /// What still escapes, measured properly this time.
    ///
    /// The command is created inside the job now — CreateProcess with
    /// PROC_THREAD_ATTRIBUTE_JOB_LIST rather than a job assigned to a process
    /// already running — so the race that was blamed for this is gone. A child
    /// `cmd` detaches through `start /b` anyway, and this test says so rather than
    /// claiming a guarantee that does not hold.
    ///
    /// Worth recording how much of the earlier version of this was wrong, because
    /// two rounds of it produced confident conclusions from a broken measurement:
    /// `timeout` fails instantly with redirected handles, so the marker was written
    /// immediately; and unquoted, the `&amp;` bound to the outer `cmd`, so the marker
    /// was written immediately again for a different reason. Both looked exactly
    /// like an escape. The vehicle is two script files now, which have no quoting
    /// or binding to get wrong.
    ///
    /// It asserts the escape, so the day somebody works out why `start /b` gets
    /// out, this goes red and says to assert containment instead.
    /// </summary>
    [Fact]
    public async Task A_child_that_detaches_through_start_still_escapes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(_root, "alive.txt");

        // Script files rather than a nested cmd line, because cmd's quoting is not
        // a reliable test vehicle and every earlier version of this test measured
        // it instead of the confinement. Unquoted, the & bound to the outer cmd and
        // the echo ran immediately; quoted, the nesting became unpredictable. Two
        // plain scripts have no ambiguity at all.
        File.WriteAllText(Path.Combine(_root, "waiter.cmd"), string.Join(Environment.NewLine,
            "@echo off",
            "ping -n 7 127.0.0.1 > nul",
            "echo alive > \"%~dp0alive.txt\""));

        File.WriteAllText(Path.Combine(_root, "spawn.cmd"), string.Join(Environment.NewLine,
            "@echo off",
            "start /b cmd /c \"%~dp0waiter.cmd\""));

        // The parent exits at once, so nothing times out and nothing is cancelled:
        // exactly the case killing the process tree never covered.
        var run = await Harness().RunCommandAsync(
            $"cmd /c {Path.Combine(_root, "spawn.cmd")}", approved: true);
        Assert.True(run.Outcome == ConciergeToolOutcome.Succeeded,
            $"{run.Summary} :: {run.Output}");

        // Long enough that the detached child would have written by now if it had
        // been allowed to live.
        await Task.Delay(TimeSpan.FromSeconds(9));

        Assert.True(File.Exists(marker),
            "A detached child was contained. Good news, and this test is now out of date: "
            + "assert containment instead of documenting the escape.");
    }

    /// <summary>
    /// And the boundary is per command, not per application: one command finishing
    /// must not kill a process another command is still using.
    /// </summary>
    [Fact]
    public async Task One_command_finishing_does_not_disturb_another()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var harness = Harness();

        var slow = harness.RunCommandAsync("cmd /c ping -n 4 127.0.0.1 > nul & echo slow done", approved: true);
        await Task.Delay(300);
        await harness.RunCommandAsync("cmd /c echo quick done", approved: true);

        var result = await slow;

        Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
        Assert.Contains("slow done", result.Output);
    }
}
