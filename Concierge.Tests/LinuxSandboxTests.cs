using System.Diagnostics;
using Concierge.Shared.Sandboxing;

namespace Concierge.Tests;

/// <summary>
/// The boundary a command runs inside on Linux.
///
/// Linux reported "not confined" and meant it — a command started with nothing
/// around it while Engineering said so honestly and nobody could do anything about
/// it. This closes it with what the system already provides: a process namespace
/// whose children die with it, and resource limits. The same two guarantees the
/// Windows job object gives, at the same numbers.
///
/// **These tests run on Windows and check the command that would be run**, because
/// the suite runs where the developer is. That is a real limit and it is why the
/// behaviour was measured on actual Linux first rather than reasoned about — this
/// repository has a history of confident sandbox reasoning that turned out to be
/// four rounds of bad measurement. Under WSL Ubuntu-24.04, kernel 6.18:
///
///   1. the wrapped command runs;
///   2. a child started with `nohup … &amp; disown` does **not** survive the
///      command — genuinely contained, not merely unobserved;
///   3. a 900MB allocation under the 512MB cap fails with MemoryError;
///   4. an ordinary pipeline still works, so the four-process cap does not break
///      the commands people actually run.
///
/// What is checked here is that the command line which produced those results is
/// the command line this builds.
/// </summary>
public sealed class LinuxSandboxTests
{
    private const string Unshare = "/usr/bin/unshare";
    private const string Prlimit = "/usr/bin/prlimit";

    private static ProcessStartInfo Wrapped(
        string? unshare = Unshare, string? prlimit = Prlimit, params string[] arguments)
    {
        var start = new ProcessStartInfo("/bin/echo");

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        new LinuxNamespaceSandbox(unshare, prlimit).Prepare(start, "/tmp/work");
        return start;
    }

    private static string Line(ProcessStartInfo start)
        => $"{start.FileName} {string.Join(' ', start.ArgumentList)}";

    // ── What actually gets run ────────────────────────────────────────────

    /// <summary>
    /// `--kill-child` is the whole point and the direct equivalent of the job
    /// object's KILL_ON_JOB_CLOSE: when the command ends, everything it started
    /// ends with it. Measured on Linux, not assumed.
    /// </summary>
    [Fact]
    public void A_command_runs_in_its_own_process_namespace_that_takes_its_children_with_it()
    {
        var line = Line(Wrapped());

        Assert.StartsWith(Unshare, line, StringComparison.Ordinal);
        Assert.Contains("--pid", line, StringComparison.Ordinal);
        Assert.Contains("--fork", line, StringComparison.Ordinal);
        Assert.Contains("--kill-child", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A process namespace normally needs root. The user namespace is what makes
    /// it available to an ordinary account, which is the only reason this works on
    /// a machine nobody has configured.
    /// </summary>
    [Fact]
    public void It_does_not_need_to_be_root()
    {
        var line = Line(Wrapped());

        Assert.Contains("--user", line, StringComparison.Ordinal);
        Assert.Contains("--map-root-user", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same 512MB and four processes the job object uses. A command refused on
    /// one machine should not sail through on another.
    /// </summary>
    [Fact]
    public void The_caps_are_the_same_as_the_ones_windows_uses()
    {
        var line = Line(Wrapped());

        Assert.Contains("--as=536870912", line, StringComparison.Ordinal);
        Assert.Contains("--nproc=4", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Cap what a command may write" sat on the task list as **not doable**. That
    /// was true of Windows job objects and was then quietly generalised to every
    /// platform without anybody checking. Linux has had it all along.
    ///
    /// Measured before being claimed, because that entry is exactly what an
    /// unmeasured claim looks like: writing 400MB under a 256MB cap stops at
    /// 268,435,456 bytes.
    /// </summary>
    [Fact]
    public void There_is_a_cap_on_what_a_command_can_write()
        => Assert.Contains("--fsize=268435456", Line(Wrapped()), StringComparison.Ordinal);

    /// <summary>
    /// It caps any one file, not everything written in total. A command determined
    /// to fill a disk can still write many files, and the wording must not suggest
    /// otherwise.
    /// </summary>
    [Fact]
    public void The_write_cap_says_what_it_actually_covers()
        => Assert.Contains(
            "any one file",
            LinuxNamespaceSandbox.Describe(true, true).Explanation,
            StringComparison.Ordinal);

    /// <summary>
    /// The command survives being wrapped, with its arguments intact and in order.
    /// A boundary that quietly drops an argument is a boundary that breaks the
    /// thing it is protecting.
    /// </summary>
    [Fact]
    public void The_command_and_its_arguments_come_through_unchanged()
    {
        var start = Wrapped(arguments: ["hello world", "--flag", "a:b"]);

        Assert.Equal(
            ["/bin/echo", "hello world", "--flag", "a:b"],
            start.ArgumentList.SkipWhile(a => a != "/bin/echo").ToArray());
    }

    /// <summary>
    /// Each wrapper hands over with <c>--</c>, so a command whose own arguments
    /// look like unshare's or prlimit's is still that command's arguments.
    /// </summary>
    [Fact]
    public void A_commands_own_arguments_cannot_be_mistaken_for_the_wrappers()
    {
        var start = Wrapped(arguments: ["--pid", "--nproc=99"]);
        var argv = start.ArgumentList.ToList();

        // Both wrappers close before the next thing begins.
        Assert.Equal(2, argv.Count(a => a == "--"));

        // And the command's own look-alike arguments sit after the last one.
        Assert.True(argv.LastIndexOf("--") < argv.IndexOf("--nproc=99"));
    }

    [Fact]
    public void The_command_starts_where_the_work_is()
        => Assert.Equal("/tmp/work", Wrapped().WorkingDirectory);

    // ── When the tools are not there ──────────────────────────────────────

    /// <summary>
    /// A boundary nobody can verify is the same as no boundary. If neither tool is
    /// installed the command runs as it did, and the capability says so — rather
    /// than wrapping with something absent and failing every command instead.
    /// </summary>
    [Fact]
    public void With_neither_tool_installed_the_command_is_left_alone()
    {
        var start = Wrapped(unshare: null, prlimit: null);

        Assert.Equal("/bin/echo", start.FileName);
        Assert.Equal(SandboxStrength.None, LinuxNamespaceSandbox.Describe(false, false).Strength);
    }

    [Fact]
    public void With_only_one_of_them_the_half_that_works_is_still_applied()
    {
        Assert.StartsWith(Unshare, Line(Wrapped(prlimit: null)), StringComparison.Ordinal);
        Assert.StartsWith(Prlimit, Line(Wrapped(unshare: null)), StringComparison.Ordinal);
    }

    [Fact]
    public void What_it_reports_matches_what_it_can_actually_do()
    {
        Assert.Equal("linux-namespaces", LinuxNamespaceSandbox.Describe(true, true).Mechanism);
        Assert.Equal("linux-pid-namespace", LinuxNamespaceSandbox.Describe(true, false).Mechanism);
        Assert.Equal("linux-rlimits", LinuxNamespaceSandbox.Describe(false, true).Mechanism);
        Assert.Equal("none", LinuxNamespaceSandbox.Describe(false, false).Mechanism);
    }

    // ── What it refuses to claim ──────────────────────────────────────────

    /// <summary>
    /// It does not restrict the filesystem, and it must not imply that it does.
    /// Doing that properly is Landlock, which has to be asked for by the child in
    /// the moment between fork and exec — somewhere managed code cannot reach
    /// without a helper binary per architecture. That is a real piece of work, and
    /// calling this "Confined" would be claiming it was already done.
    /// </summary>
    [Fact]
    public void It_does_not_claim_a_filesystem_boundary_it_does_not_have()
    {
        var capability = LinuxNamespaceSandbox.Describe(true, true);

        Assert.Equal(SandboxStrength.Process, capability.Strength);
        Assert.NotEqual(SandboxStrength.Confined, capability.Strength);
        Assert.Contains("read what the app can read", capability.Explanation, StringComparison.Ordinal);
        Assert.Contains("Landlock", capability.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// It says the same as Windows because it does the same as Windows. Two heads
    /// giving the same guarantee and describing it differently is how somebody
    /// comes to believe one is safer than the other.
    /// </summary>
    [Fact]
    public void Linux_and_windows_claim_the_same_strength()
        => Assert.Equal(
            SandboxStrength.Process,
            LinuxNamespaceSandbox.Describe(true, true).Strength);

    /// <summary>
    /// Android is Linux and is deliberately not this. A child there runs under the
    /// app's own user id, and wrapping it in unshare would report a boundary the
    /// platform does not give — the exact failure this whole file exists to stop.
    /// </summary>
    [Fact]
    public void Android_is_still_told_the_truth_about_itself()
    {
        var here = CodeSandbox.DescribeCurrentPlatform();

        Assert.False(string.IsNullOrWhiteSpace(here.Explanation));

        if (OperatingSystem.IsAndroid())
        {
            Assert.Equal(SandboxStrength.None, here.Strength);
        }
    }

    /// <summary>
    /// Everything is in place before the process starts. A boundary applied after
    /// a process is already running is a race the child gets to act inside — the
    /// lesson the Windows side learned by moving to PROC_THREAD_ATTRIBUTE_JOB_LIST.
    /// </summary>
    [Fact]
    public void There_is_no_window_between_starting_and_being_confined()
    {
        var start = Wrapped();

        // The thing started is the wrapper, not the command — so there is no
        // moment at which the command exists unwrapped.
        Assert.Equal(Unshare, start.FileName);
        Assert.NotEqual("/bin/echo", start.FileName);
    }
}
