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
    /// What the boundary does not catch, named so it is not mistaken for something
    /// it does.
    ///
    /// The job object is created and the process assigned to it *after* the process
    /// has started, because System.Diagnostics.Process cannot start one suspended.
    /// A command whose first act is to detach a background child wins that race:
    /// the grandchild exists before the assignment lands, and a process outside the
    /// job is not killed when the job closes.
    ///
    /// Measured, not assumed — this test starts red against wishful thinking. The
    /// marker file appearing means the detached child outlived the command that
    /// started it, which is exactly what happens today.
    ///
    /// Fixing it properly means starting the process suspended through CreateProcess
    /// and resuming it after assignment, which is real interop and is on the list
    /// rather than half-done here. Recording it as a passing test that documents a
    /// limitation beats a failing one nobody reads, and beats silence.
    /// </summary>
    [Fact]
    public async Task A_child_that_detaches_before_confinement_lands_still_escapes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(_root, $"escaped-{Guid.NewGuid():N}.txt");

        // ping rather than timeout as the delay: timeout needs a console and fails
        // instantly when its handles are redirected, which made an earlier version
        // of this test fail against confinement that was working.
        var command = $"cmd /c start /b cmd /c ping -n 7 127.0.0.1 > nul & echo alive > \"{marker}\"";

        Assert.Equal(ConciergeToolOutcome.Succeeded,
            (await Harness().RunCommandAsync(command, approved: true)).Outcome);

        await Task.Delay(TimeSpan.FromSeconds(9));

        Assert.True(File.Exists(marker),
            "A detached child was contained. Good news, and this test is now out of date: "
            + "the confinement got stronger, so assert containment instead of documenting the gap.");
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
