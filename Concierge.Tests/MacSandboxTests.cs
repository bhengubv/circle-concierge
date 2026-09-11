using System.Diagnostics;
using Concierge.Shared.Sandboxing;

namespace Concierge.Tests;

/// <summary>
/// The boundary a command would get on a Mac.
///
/// **None of this has been run on one, and that is the first thing to say.** There is no Mac
/// here, so what these check is the command line that would be built and the capability that
/// would be reported — not that the kernel honours either. This repository's history contains
/// four rounds of confident sandbox reasoning that were all wrong until somebody measured, so
/// the honest position is that this is written from the documented behaviour of `sh`,
/// `ulimit` and `sandbox-exec` and is waiting for a machine.
///
/// What *can* be checked without a Mac is the part that has actually gone wrong before: a
/// sandbox that reports a boundary it does not have. So the capability is pinned to what each
/// combination of tools can enforce, and the profile is pinned to denying writes.
/// </summary>
public sealed class MacSandboxTests
{
    // ── What it says it can do ────────────────────────────────────────────

    /// <summary>
    /// Reporting Confined on the strength of resource limits alone would be the exact
    /// overstatement the Linux side refuses to make about Landlock.
    /// </summary>
    [Fact]
    public void Limits_alone_are_a_process_boundary_and_not_a_confined_one()
    {
        var capability = MacSandbox.Describe(hasShell: true, hasSandboxExec: false);

        Assert.Equal(SandboxStrength.Process, capability.Strength);
        Assert.Equal("ulimit", capability.Mechanism);
    }

    [Fact]
    public void With_the_sandbox_as_well_it_is_confined()
    {
        var capability = MacSandbox.Describe(hasShell: true, hasSandboxExec: true);

        Assert.Equal(SandboxStrength.Confined, capability.Strength);
        Assert.Contains("sandbox-exec", capability.Mechanism, StringComparison.Ordinal);
    }

    [Fact]
    public void With_neither_it_says_there_is_no_boundary_at_all()
        => Assert.Equal(
            SandboxStrength.None,
            MacSandbox.Describe(hasShell: false, hasSandboxExec: false).Strength);

    /// <summary>
    /// Windows kills what a command leaves behind and Linux does too. macOS does not, and a
    /// capability that did not say so would be a screen asserting something untrue — which is
    /// the defect this whole product is measured against.
    /// </summary>
    [Fact]
    public void It_says_out_loud_that_what_a_command_detaches_can_outlive_it()
    {
        foreach (var capability in new[]
                 {
                     MacSandbox.Describe(true, true),
                     MacSandbox.Describe(true, false),
                 })
        {
            Assert.Contains("outlive", capability.Explanation, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── The profile ───────────────────────────────────────────────────────

    [Fact]
    public void The_profile_denies_writing_and_then_allows_the_workspace_back()
    {
        var profile = MacSandbox.ProfileFor("/Users/somebody/work");

        Assert.Contains("(deny file-write*)", profile, StringComparison.Ordinal);
        Assert.Contains("(subpath \"/Users/somebody/work\")", profile, StringComparison.Ordinal);

        // Reading is left alone on purpose: a command that cannot read the system cannot
        // compile anything, and what is being protected is what gets changed.
        Assert.DoesNotContain("deny file-read", profile, StringComparison.Ordinal);
    }

    /// <summary>
    /// A workspace path can contain a quote, and a profile built by pasting one in would stop
    /// being a profile — which on a deny-by-exception rule means the command runs with the
    /// deny and without the allow, or not at all.
    /// </summary>
    [Fact]
    public void A_path_with_a_quote_in_it_does_not_break_the_profile()
    {
        var profile = MacSandbox.ProfileFor("/Users/some\"body/work");

        Assert.Contains("some\\\"body", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void Temporary_folders_stay_writable_because_everything_uses_them()
    {
        var profile = MacSandbox.ProfileFor("/work");

        Assert.Contains("/private/tmp", profile, StringComparison.Ordinal);
        Assert.Contains("/dev/null", profile, StringComparison.Ordinal);
    }

    // ── The command it would build ────────────────────────────────────────

    /// <summary>
    /// On this machine neither tool exists, so nothing is wrapped — and the command has to be
    /// left exactly as it was rather than half-wrapped with something that is not there.
    /// </summary>
    [Fact]
    public void Where_neither_tool_is_present_the_command_is_left_alone()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        var start = new ProcessStartInfo("dotnet");
        start.ArgumentList.Add("--version");

        new MacSandbox().Prepare(start, Path.GetTempPath());

        Assert.Equal("dotnet", start.FileName);
        Assert.Equal(["--version"], start.ArgumentList);
        Assert.Equal(Path.GetTempPath(), start.WorkingDirectory);
    }

    [Fact]
    public void And_it_reports_no_boundary_rather_than_pretending()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal(SandboxStrength.None, new MacSandbox().Capability.Strength);
    }

    /// <summary>
    /// Mac Catalyst is the desktop head and gets the same boundary; iOS never reaches here
    /// because it forbids child processes at all.
    /// </summary>
    [Fact]
    public void The_platform_chooser_knows_about_macs()
    {
        var capability = CodeSandbox.DescribeCurrentPlatform();

        // On this machine that is the Windows job object — what is being checked is that
        // asking does not throw and that whatever comes back names itself.
        Assert.False(string.IsNullOrWhiteSpace(capability.Mechanism));
        Assert.False(string.IsNullOrWhiteSpace(capability.Explanation));
    }
}
