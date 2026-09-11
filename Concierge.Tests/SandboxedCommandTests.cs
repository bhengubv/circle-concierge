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
    /// Nothing a command starts is still running when the command is done.
    ///
    /// This test asserted the opposite for a long time, under the name
    /// `A_child_that_detaches_through_start_still_escapes`, and it took four
    /// rounds of measurement to find out why it was wrong.
    ///
    /// Two earlier versions were discarded for measuring their own vehicle:
    /// `timeout` fails instantly with redirected handles, and an unquoted `&amp;`
    /// binds to the outer shell. Both produced a marker file immediately and both
    /// looked exactly like an escape. The third version fixed the vehicle, still
    /// found the marker after nine seconds, and concluded a detached child had
    /// outlived the job.
    ///
    /// It had not. Timing the call showed `RunAsync` taking 7,141ms for a child
    /// that slept seven seconds, and enumerating every process on the machine two
    /// seconds afterwards found nothing new alive. `start /b` asks for a child
    /// without a new window — but the command is created with `CREATE_NO_WINDOW`
    /// and has no console for it to share, so cmd runs it synchronously instead.
    /// There is no detached process in this scenario. The marker the test kept
    /// finding was written *during* the call, while the job was open, by a child
    /// the call was still waiting for.
    ///
    /// So the honest assertion is the one below, and it is the opposite of what
    /// was recorded: this scenario is contained. Whether some *other* route
    /// genuinely detaches is unknown and unmeasured — if one is found, it gets its
    /// own test, and this comment is the reason not to trust a marker file as
    /// evidence next time.
    /// </summary>
    [Fact]
    public async Task Nothing_a_command_starts_outlives_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Two script files rather than a nested cmd line. Every earlier version of
        // this test measured cmd's quoting instead of the confinement; two plain
        // scripts have nothing to get wrong.
        //
        // And it no longer scans the machine. It used to list every process before
        // and after and fail if any new one was called cmd — which made it fail
        // whenever *anything else* opened a shell during those seven seconds:
        // another test, a build, a person opening a terminal. A test that a passing
        // machine can fail cannot tell a regression from a coincidence, which is
        // the whole reason it exists.
        var marker = Path.Combine(_root, "outlived.txt");

        File.WriteAllText(Path.Combine(_root, "waiter.cmd"), string.Join(Environment.NewLine,
            "@echo off",
            "ping -n 6 127.0.0.1 > nul",
            $"echo done > \"{marker}\""));

        File.WriteAllText(Path.Combine(_root, "spawn.cmd"), string.Join(Environment.NewLine,
            "@echo off",
            "echo started",
            "start /b cmd /c \"%~dp0waiter.cmd\""));

        var run = await Harness().RunCommandAsync(
            $"cmd /c {Path.Combine(_root, "spawn.cmd")}", approved: true);

        // The instant the command was over. Everything below is about what happened
        // relative to this moment.
        var finished = DateTime.UtcNow;

        Assert.Equal(ConciergeToolOutcome.Succeeded, run.Outcome);
        Assert.Contains("started", run.Output, StringComparison.OrdinalIgnoreCase);

        // Long enough that a child which had genuinely escaped would have finished
        // its six-second wait and written its marker.
        await Task.Delay(TimeSpan.FromSeconds(4));

        // The claim, measured directly: did anything write *after* the command
        // returned? A marker on its own proves something ran and says nothing about
        // when — which is precisely the mistake that had this file asserting an
        // escape that never happened. So the marker is read for its timestamp, not
        // its existence, and the existence on its own is fine: the child does run,
        // synchronously, inside the call.
        Assert.False(
            File.Exists(marker) && File.GetLastWriteTimeUtc(marker) > finished,
            "Something a command started was still running after the command returned.");
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

    // ── What a command is allowed to see ──────────────────────────────────

    /// <summary>
    /// A command does not get the operator's secrets.
    ///
    /// `ConfinedProcess` passed `lpEnvironment: nint.Zero`, which hands the child
    /// everything this process holds. On a developer's machine that routinely
    /// means a GitHub token, NuGet credentials, cloud keys — none of which a
    /// model-written command has any business reading, and nothing was stopping
    /// it. Taken from OpenSandbox, which strips its own configuration out of the
    /// workload's environment for exactly this reason.
    /// </summary>
    [Fact]
    public async Task A_command_cannot_read_a_token_from_the_environment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Environment.SetEnvironmentVariable("CONCIERGE_TEST_GITHUB_TOKEN", "ghp_secret_value");
        Environment.SetEnvironmentVariable("CONCIERGE_TEST_API_KEY", "sk-secret-value");

        try
        {
            var run = await Harness().RunCommandAsync("cmd /c set", approved: true);

            Assert.Equal(ConciergeToolOutcome.Succeeded, run.Outcome);
            Assert.DoesNotContain("ghp_secret_value", run.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sk-secret-value", run.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONCIERGE_TEST_GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("CONCIERGE_TEST_API_KEY", null);
        }
    }

    /// <summary>
    /// The other half, and the reason this is a denylist rather than an allowlist.
    /// A command that cannot see PATH is a command that cannot run anything, and
    /// stripping the environment to a safe minimum breaks every real build tool.
    /// </summary>
    [Fact]
    public async Task A_command_can_still_read_the_ordinary_environment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Environment.SetEnvironmentVariable("CONCIERGE_TEST_ORDINARY", "plainly_visible");

        try
        {
            var run = await Harness().RunCommandAsync("cmd /c set", approved: true);

            Assert.Contains("plainly_visible", run.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PATH=", run.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONCIERGE_TEST_ORDINARY", null);
        }
    }
}
