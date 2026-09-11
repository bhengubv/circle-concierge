using System.Diagnostics;

namespace Concierge.Shared.Sandboxing;

/// <summary>
/// The boundary a command runs inside on Linux.
///
/// Linux reported "not confined" and meant it: `run_command` on a Linux head
/// started a process with nothing around it while Engineering said so honestly and
/// nobody could do anything about it. This closes that with the two things the
/// system already provides and no package adds — a process namespace whose
/// children die with it, and resource limits.
///
/// **The two guarantees, and they are the same two Windows gives.** A command runs
/// in its own process namespace with `--kill-child`, so nothing it starts outlives
/// it — the direct equivalent of `KILL_ON_JOB_CLOSE`. And it runs under memory and
/// process caps, the equivalent of the job object's. Same caps, same numbers, on
/// purpose: a command that is refused on one machine should not sail through on
/// another.
///
/// **What this is not, said plainly.** It does not restrict the filesystem. A
/// command can still read what the app can read. Doing that properly is Landlock,
/// and Landlock has to be asked for by the child itself in the moment between fork
/// and exec — which managed code cannot reach without a small helper binary
/// compiled per architecture. That is a real piece of work and a real decision, not
/// something to imply by calling this <see cref="SandboxStrength.Confined"/>. So it
/// reports <see cref="SandboxStrength.Process"/>, exactly as Windows does, and
/// Engineering says which.
///
/// OpenSandbox gets its filesystem boundary from bubblewrap, which is a package
/// this does not assume is installed. Where `unshare` or `prlimit` is missing, that
/// half is dropped and the capability says so rather than pretending — a boundary
/// nobody can verify is the same as no boundary.
/// </summary>
public sealed class LinuxNamespaceSandbox : ICodeSandbox
{
    /// <summary>The same 512MB the job object caps at, so the two heads agree.</summary>
    private const long MemoryCapBytes = 512L * 1024 * 1024;

    /// <summary>And the same four processes.</summary>
    private const int ProcessCap = 4;

    /// <summary>
    /// The largest file a command may write.
    ///
    /// The task list carried "cap what a command may write" as **not doable** —
    /// which was true of job objects and was then quietly generalised to every
    /// platform without anybody checking. Linux has had it all along: `prlimit`
    /// sets RLIMIT_FSIZE and the kernel enforces it.
    ///
    /// Measured before claiming it, because that entry is what a claim looks like
    /// when nobody measures. Writing 400MB under a 256MB cap stops at exactly
    /// 268,435,456 bytes.
    ///
    /// **What it is, precisely:** a cap on any single file, not a quota on
    /// everything a command writes in total. A command determined to fill a disk
    /// can still write many files. It stops the ordinary accident — a runaway log,
    /// a bad download, an infinite loop appending to one file — which is what this
    /// was ever going to catch.
    /// </summary>
    private const long FileCapBytes = 256L * 1024 * 1024;

    private readonly string? _unshare;
    private readonly string? _prlimit;

    /// <param name="unshare">Where <c>unshare</c> is, or null. For tests.</param>
    /// <param name="prlimit">Where <c>prlimit</c> is, or null. For tests.</param>
    public LinuxNamespaceSandbox(string? unshare = null, string? prlimit = null)
    {
        _unshare = unshare ?? Which("unshare");
        _prlimit = prlimit ?? Which("prlimit");

        Capability = Describe(_unshare is not null, _prlimit is not null);
    }

    /// <inheritdoc />
    public SandboxCapability Capability { get; }

    /// <summary>What a sandbox with these two tools present can enforce.</summary>
    internal static SandboxCapability Describe(bool hasUnshare, bool hasPrlimit)
    {
        if (!hasUnshare && !hasPrlimit)
        {
            return new SandboxCapability(
                SandboxStrength.None,
                "none",
                "Neither unshare nor prlimit is installed, so a command would run with everything the app can reach.");
        }

        var parts = new List<string>();

        if (hasUnshare)
        {
            parts.Add("nothing it starts outlives it");
        }

        if (hasPrlimit)
        {
            parts.Add(
                $"it is capped at {MemoryCapBytes / 1024 / 1024}MB of memory, {ProcessCap} processes, "
                + $"and {FileCapBytes / 1024 / 1024}MB for any one file it writes");
        }

        return new SandboxCapability(
            SandboxStrength.Process,
            hasUnshare && hasPrlimit ? "linux-namespaces" : hasUnshare ? "linux-pid-namespace" : "linux-rlimits",
            $"A command runs in its own process namespace: {string.Join(", and ", parts)}. "
            + "It can still read what the app can read — restricting the filesystem needs Landlock, "
            + "which is not in place.");
    }

    /// <inheritdoc />
    public void Prepare(ProcessStartInfo startInfo, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        startInfo.WorkingDirectory = workspaceRoot;

        if (_unshare is null && _prlimit is null)
        {
            // Nothing to wrap with. The capability already says so, and running the
            // command unwrapped while claiming otherwise is the failure this whole
            // type exists to avoid.
            return;
        }

        // The command as it stands, before anything is put in front of it.
        var command = new List<string> { startInfo.FileName };
        command.AddRange(startInfo.ArgumentList);

        if (_prlimit is not null)
        {
            command.InsertRange(0, [
                _prlimit,
                $"--nproc={ProcessCap}",
                $"--as={MemoryCapBytes}",
                $"--fsize={FileCapBytes}",
                "--",
            ]);
        }

        if (_unshare is not null)
        {
            // -Ur maps this user into a new user namespace, which is what makes a
            // process namespace available without being root. --kill-child is the
            // whole point: when the command ends, everything it started ends with
            // it.
            command.InsertRange(0, [
                _unshare,
                "--user",
                "--map-root-user",
                "--pid",
                "--fork",
                "--kill-child",
                "--",
            ]);
        }

        startInfo.FileName = command[0];
        startInfo.ArgumentList.Clear();

        foreach (var argument in command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    /// <inheritdoc />
    public void Confine(Process process)
    {
        // Everything is in place before the process starts, which is deliberate and
        // the same lesson the Windows side learned: a boundary applied after a
        // process is already running is a race, and the child gets to act inside
        // it. Here there is no window at all — the command is not the thing that
        // was started, `unshare` is.
    }

    /// <summary>Where a tool is, or null. No shell, and no PATH parsing games.</summary>
    private static string? Which(string name)
    {
        foreach (var folder in new[] { "/usr/bin", "/bin", "/usr/local/bin", "/sbin", "/usr/sbin" })
        {
            var candidate = System.IO.Path.Combine(folder, name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
