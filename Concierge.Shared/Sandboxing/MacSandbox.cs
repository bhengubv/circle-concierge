using System.Diagnostics;

namespace Concierge.Shared.Sandboxing;

/// <summary>
/// The boundary a command gets on a Mac.
///
/// macOS has no `unshare` and no `prlimit`, and it has no job objects. What it does have is
/// `ulimit` in its own shell and `sandbox-exec`, and the two do different halves of the job —
/// so this uses both when both are there and says which it got.
///
/// **`ulimit` caps what a command may use**: address space, how many processes it may start,
/// and how large a file it may write. Applied by running the command through `/bin/sh`, which
/// sets the limits and then *replaces itself* with the command — `exec`, so there is no extra
/// shell sitting in the middle to be killed separately or to miscount the process cap.
///
/// **`sandbox-exec` is what stops it writing outside the workspace.** It is deprecated by
/// Apple and has been for years, and it is also still present on every macOS this would run
/// on, still used by Apple's own tooling, and the only thing short of a full app sandbox that
/// confines a child process. Deprecated and working beats absent: if a future macOS removes
/// it, `Capability` drops to what `ulimit` alone gives and says so, rather than reporting a
/// boundary that is no longer there.
///
/// **What it does not give, said plainly: killing what a command leaves behind.** Windows
/// gets that from a job object and Linux from `--kill-child`, and macOS has no equivalent
/// that does not mean writing a launchd job or a kernel extension. A command that detaches a
/// child here outlives its parent, and the caller kills the process tree instead, which is a
/// best effort rather than a guarantee.
///
/// **None of this has been run.** There is no Mac here. Everything below is written from the
/// documented behaviour of `sh`, `ulimit` and `sandbox-exec`, and the capability it reports
/// is what it *would* enforce. This file is the first thing to check against a real machine,
/// and the four rounds of confident sandbox reasoning in this repository's history — every
/// one of them wrong until it was measured — are why that sentence is here rather than a
/// quiet claim of support.
/// </summary>
public sealed class MacSandbox : ICodeSandbox
{
    /// <summary>The same 512MB the other two cap at, so the three heads agree.</summary>
    private const long MemoryCapBytes = 512L * 1024 * 1024;

    /// <summary>The same four processes.</summary>
    private const int ProcessCap = 4;

    /// <summary>The same 256MB per file.</summary>
    private const long FileCapBytes = 256L * 1024 * 1024;

    private readonly string? _shell;
    private readonly string? _sandboxExec;

    public MacSandbox()
    {
        _shell = Where("/bin/sh");
        _sandboxExec = Where("/usr/bin/sandbox-exec");

        Capability = Describe(_shell is not null, _sandboxExec is not null);
    }

    /// <inheritdoc />
    public SandboxCapability Capability { get; }

    /// <summary>What a Mac with these two present can enforce.</summary>
    internal static SandboxCapability Describe(bool hasShell, bool hasSandboxExec)
    {
        if (!hasShell && !hasSandboxExec)
        {
            return new SandboxCapability(
                SandboxStrength.None,
                "none",
                "Neither /bin/sh nor sandbox-exec is here, so a command would run with everything the app can reach.");
        }

        // Confined only when the filesystem half is actually in place. Reporting Confined on
        // the strength of resource limits alone would be the exact overstatement the Linux
        // side refuses to make about Landlock.
        if (hasSandboxExec)
        {
            return new SandboxCapability(
                SandboxStrength.Confined,
                hasShell ? "sandbox-exec + ulimit" : "sandbox-exec",
                "A command is confined to the workspace by sandbox-exec"
                + (hasShell ? ", and capped at 512MB, four processes and 256MB per file" : string.Empty)
                + ". It is not killed with its parent — macOS has no equivalent of a job object — "
                + "so anything it detaches can outlive it.");
        }

        return new SandboxCapability(
            SandboxStrength.Process,
            "ulimit",
            "A command is capped at 512MB, four processes and 256MB per file. It can still read "
            + "and write what the app can, and anything it detaches can outlive it.");
    }

    /// <inheritdoc />
    public void Prepare(ProcessStartInfo startInfo, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        startInfo.WorkingDirectory = workspaceRoot;

        if (_shell is null && _sandboxExec is null)
        {
            // Nothing to wrap with, and the capability already says so. Running the command
            // unwrapped while claiming otherwise is the failure this whole type exists to
            // avoid.
            return;
        }

        var command = new List<string> { startInfo.FileName };
        command.AddRange(startInfo.ArgumentList);

        if (_shell is not null)
        {
            // `exec "$0" "$@"` replaces the shell with the command, so the limits apply to
            // the command itself and there is no leftover shell in the middle. The arguments
            // are passed as arguments rather than pasted into the script, which is what keeps
            // a filename with a space or a quote in it from becoming two words.
            command =
            [
                _shell,
                "-c",
                $"ulimit -v {MemoryCapBytes / 1024} 2>/dev/null; "
                + $"ulimit -u {ProcessCap} 2>/dev/null; "
                + $"ulimit -f {FileCapBytes / 512} 2>/dev/null; "
                + "exec \"$0\" \"$@\"",
                .. command,
            ];
        }

        if (_sandboxExec is not null)
        {
            command.InsertRange(0, [_sandboxExec, "-p", ProfileFor(workspaceRoot)]);
        }

        startInfo.FileName = command[0];
        startInfo.ArgumentList.Clear();

        foreach (var argument in command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    /// <summary>
    /// The sandbox profile: everything allowed except writing outside the workspace.
    ///
    /// **Deny-by-exception rather than allow-by-exception, and that is the weaker choice made
    /// deliberately** — the same trade the environment scrubbing made, for the same reason. A
    /// profile that allowed only what a build needs would have to name every path a compiler,
    /// a package manager and a toolchain touch, and the first one missed turns a working
    /// command into a mysterious failure. This stops the ordinary accident — a command
    /// writing over somebody's home directory — and does not pretend to stop a determined one.
    ///
    /// Reading is left alone on purpose: a command that cannot read the system cannot compile
    /// anything, and the thing being protected here is what gets *changed*.
    /// </summary>
    internal static string ProfileFor(string workspaceRoot)
    {
        // Escaped for the profile's own string syntax, which is Scheme-like: a backslash and a
        // double quote are the two that matter, and a workspace path can contain both.
        var root = (workspaceRoot ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return
            "(version 1)"
            + "(allow default)"
            + "(deny file-write*)"
            + $"(allow file-write* (subpath \"{root}\"))"
            + "(allow file-write* (subpath \"/private/tmp\") (subpath \"/private/var/tmp\") (subpath \"/tmp\"))"
            + "(allow file-write-data (literal \"/dev/null\") (literal \"/dev/stdout\") (literal \"/dev/stderr\"))";
    }

    /// <inheritdoc />
    public void Confine(Process process)
    {
        // Everything is in place before the process starts, the same as the other two. A
        // boundary applied after a process is running is a race the child gets to act inside.
    }

    private static string? Where(string path) => File.Exists(path) ? path : null;
}
