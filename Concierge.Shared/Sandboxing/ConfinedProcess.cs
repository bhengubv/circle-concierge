using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Concierge.Shared.Sandboxing;

/// <summary>What a confined command printed, and how it ended.</summary>
/// <param name="ExitCode">The process's exit code.</param>
/// <param name="Output">Standard output and standard error, in that order.</param>
public sealed record ConfinedResult(int ExitCode, string Output);

/// <summary>
/// Starting a process that is already inside a job object.
///
/// A job object applied after a process has started cannot contain what that
/// process has already spawned, and `Process.Start` gives no way to intervene
/// between creation and execution. `PROC_THREAD_ATTRIBUTE_JOB_LIST` removes that
/// window rather than narrowing it: the process is created *in* the job. Preferred
/// over the more common CREATE_SUSPENDED-assign-resume dance for the same reason —
/// suspending still creates the process first, and a suspend that fails leaves
/// something running that nobody is confining.
///
/// **It does not close the detach gap, and the summary said it did until the
/// measurement was fixed.** A child `cmd` still gets out through `start /b`, which
/// is what `A_child_that_detaches_through_start_still_escapes` records. Two earlier
/// attempts to measure this produced confident conclusions from broken vehicles —
/// `timeout` fails instantly with redirected handles, and an unquoted `&` binds to
/// the outer shell — so both times a marker file appeared for reasons that had
/// nothing to do with confinement. Worth knowing before trusting the next
/// explanation of why `start` escapes.
///
/// The cost is honest and worth stating: this hand-rolls the pipes that
/// `Process.Start` would have given for free, and hand-rolled pipes are where
/// deadlocks and handle leaks live. Both reads are asynchronous and both ends are
/// closed in a finally, because the classic failure — waiting for exit while the
/// child blocks writing to a full pipe nobody is draining — hangs the app rather
/// than failing it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConfinedProcess
{
    /// <summary>
    /// Runs a command inside <paramref name="job"/>, from creation.
    /// </summary>
    /// <param name="job">A job object handle the process is born into.</param>
    /// <param name="executable">The program.</param>
    /// <param name="arguments">Arguments, already split.</param>
    /// <param name="workingDirectory">Where it runs.</param>
    public static async Task<ConfinedResult> RunAsync(
        nint job,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };

        nint outRead = 0, outWrite = 0, errRead = 0, errWrite = 0;
        nint attributes = 0;
        nint jobHandleBuffer = 0;
        nint environment = 0;
        var info = new ProcessInformation();

        try
        {
            if (!CreatePipe(out outRead, out outWrite, ref security, 0)
                || !CreatePipe(out errRead, out errWrite, ref security, 0))
            {
                throw new InvalidOperationException("The command's output could not be captured, so it was not run.");
            }

            // Our ends must not be inherited, or the child holds a copy of the read
            // handle and the pipe never reports end-of-file — the read hangs after
            // the process has exited, which looks exactly like a slow command.
            SetHandleInformation(outRead, HandleFlagInherit, 0);
            SetHandleInformation(errRead, HandleFlagInherit, 0);

            attributes = BuildJobAttribute(job, out jobHandleBuffer);

            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            startup.StartupInfo.dwFlags = StartFUseStdHandles;
            startup.StartupInfo.hStdOutput = outWrite;
            startup.StartupInfo.hStdError = errWrite;
            startup.StartupInfo.hStdInput = GetStdHandle(StdInput);
            startup.lpAttributeList = attributes;

            // CreateProcess may write to the command line buffer, so it cannot be a
            // literal. Quoted the way Windows parses it, which is not the way most
            // people assume.
            var commandLine = new StringBuilder(CommandLineFor(executable, arguments));

            // A filtered copy of our environment rather than a wholesale
            // inheritance. Passing zero here hands the command everything this
            // process holds — on a developer's machine that routinely means their
            // GitHub token, their NuGet credentials, their cloud keys. Nothing in
            // Concierge needed a model-written command to have those, and nothing
            // was stopping it.
            environment = BuildEnvironment();

            var created = CreateProcess(
                null,
                commandLine,
                nint.Zero,
                nint.Zero,
                bInheritHandles: true,
                dwCreationFlags: CreateNoWindow | ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                lpEnvironment: environment,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: ref startup,
                lpProcessInformation: out info);

            if (!created)
            {
                throw new InvalidOperationException(
                    $"The command could not be started confined: {Marshal.GetLastWin32Error()}.");
            }

            // Closed immediately: while we hold the write ends, the pipes never see
            // end-of-file even after the child has gone.
            CloseHandle(outWrite);
            CloseHandle(errWrite);
            outWrite = errWrite = 0;

            // Waited on by the handle we already own, not by looking the process up again.
            //
            // This was `Process.GetProcessById(info.dwProcessId)`, which throws
            // "Process with an Id of N is not running" when the child has already finished —
            // and a fast command finishes well inside the time it takes to get here. So
            // `run_command` did not return a fast command's output, it threw: an exception
            // out of the sandbox, for a command that worked perfectly. It surfaced about one
            // run in three under a loaded machine and would do the same to anybody running
            // `echo` on a busy laptop.
            //
            // The handle cannot race. We hold it from CreateProcess until the finally closes
            // it, it stays valid after the child exits, and it is signalled the moment the
            // child ends.
            var output = ReadAllAsync(outRead, cancellationToken);
            var error = ReadAllAsync(errRead, cancellationToken);

            await WaitAsync(info.hProcess, cancellationToken).ConfigureAwait(false);

            if (!GetExitCodeProcess(info.hProcess, out var exitCode))
            {
                throw new InvalidOperationException(
                    $"The command ended and its exit code could not be read: {Marshal.GetLastWin32Error()}.");
            }

            return new ConfinedResult(exitCode, await output + await error);
        }
        finally
        {
            foreach (var handle in new[] { outRead, outWrite, errRead, errWrite, info.hThread, info.hProcess })
            {
                if (handle != 0)
                {
                    CloseHandle(handle);
                }
            }

            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (jobHandleBuffer != 0)
            {
                Marshal.FreeHGlobal(jobHandleBuffer);
            }

            if (environment != 0)
            {
                Marshal.FreeHGlobal(environment);
            }
        }
    }

    /// <summary>
    /// Names whose value is almost certainly a secret.
    ///
    /// Matched as substrings, case-insensitively, because the interesting ones are
    /// never spelled the same twice: `GITHUB_TOKEN`, `NUGET_AUTH_TOKEN`,
    /// `AWS_SECRET_ACCESS_KEY`, `ANTHROPIC_API_KEY`, `npm_config__authToken`.
    ///
    /// Deliberately a denylist and not an allowlist, which is the weaker choice
    /// and the right one here. An allowlist would have to name every variable a
    /// build tool needs — PATH, TEMP, USERPROFILE, PROCESSOR_ARCHITECTURE,
    /// VSINSTALLDIR, half of MSBuild's surface — and the first one missed turns a
    /// working command into a mysterious failure. A denylist fails the other way:
    /// it can miss a secret nobody thought of. That is a real limitation and it is
    /// why this is a reduction in blast radius rather than a guarantee.
    /// </summary>
    private static readonly string[] Secretish =
    [
        "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL",
        "APIKEY", "API_KEY", "_KEY", "PRIVATE", "SESSION",
    ];

    /// <summary>
    /// This process's environment, minus anything that looks like a secret,
    /// laid out the way CreateProcess wants it.
    ///
    /// The block is NAME=VALUE pairs, each null-terminated, with a second null at
    /// the end. Unicode, which is why the call adds CREATE_UNICODE_ENVIRONMENT —
    /// without that flag Windows reads the same bytes as ANSI and the command gets
    /// an environment of mojibake.
    /// </summary>
    private static nint BuildEnvironment()
    {
        var block = new StringBuilder();

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value)
            {
                continue;
            }

            if (Secretish.Any(mark => name.Contains(mark, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Variables whose name starts with '=' are Windows' per-drive current
            // directories (`=C:`). They are legal, they matter to cmd, and a
            // naive writer that skips them changes where a relative path resolves.
            block.Append(name).Append('=').Append(value).Append(' ');
        }

        block.Append(' ');

        return Marshal.StringToHGlobalUni(block.ToString());
    }

    /// <summary>
    /// The attribute list that puts the process in the job at creation.
    /// </summary>
    private static nint BuildJobAttribute(nint job, out nint jobHandleBuffer)
    {
        jobHandleBuffer = 0;
        nint size = 0;
        InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);

        var list = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(list);
            throw new InvalidOperationException("The confinement could not be prepared, so the command was not run.");
        }

        // The handle has to stay alive and pinned for the duration of the call,
        // which it does: the caller owns the job for the length of the command.
        // Freed by the caller once CreateProcess has returned: the attribute list
        // holds a pointer to this, so releasing it any earlier hands CreateProcess
        // a dangling one.
        var jobHandle = Marshal.AllocHGlobal(nint.Size);
        Marshal.WriteIntPtr(jobHandle, job);
        jobHandleBuffer = jobHandle;

        if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeJobList, jobHandle, nint.Size, nint.Zero, nint.Zero))
        {
            Marshal.FreeHGlobal(jobHandle);
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new InvalidOperationException("The confinement could not be applied, so the command was not run.");
        }

        return list;
    }

    private static async Task<string> ReadAllAsync(nint pipe, CancellationToken cancellationToken)
    {
        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(pipe, ownsHandle: false);
        await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A command line Windows will parse back into the arguments given.
    ///
    /// Its rules are not the obvious ones — backslashes only escape a quote when
    /// they immediately precede one — and getting this wrong is how a path with a
    /// space becomes two arguments.
    /// </summary>
    internal static string CommandLineFor(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        Append(line, executable);

        foreach (var argument in arguments ?? [])
        {
            line.Append(' ');
            Append(line, argument);
        }

        return line.ToString();
    }

    private static void Append(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"'))
        {
            line.Append(argument);
            return;
        }

        line.Append('"');

        for (var i = 0; i < argument.Length; i++)
        {
            var slashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                slashes++;
                i++;
            }

            if (i == argument.Length)
            {
                line.Append('\\', slashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                line.Append('\\', slashes * 2 + 1);
            }
            else
            {
                line.Append('\\', slashes);
            }

            line.Append(argument[i]);
        }

        line.Append('"');
    }

    // ── Interop ───────────────────────────────────────────────────────────

    private const int HandleFlagInherit = 0x00000001;
    private const int StartFUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StdInput = -10;
    private static readonly nint ProcThreadAttributeJobList = 0x0002000D;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out nint read, out nint write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(nint handle, int mask, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out int exitCode);

    /// <summary>
    /// Waits for a process handle to be signalled, without a thread parked on it.
    ///
    /// `ThreadPool.RegisterWaitForSingleObject` hands the wait to the operating system and
    /// calls back when the handle is signalled, so a long-running command costs no thread.
    /// The registration is unregistered on both paths — a wait left registered holds the
    /// handle and fires against a closed one later.
    /// </summary>
    private static async Task WaitAsync(nint process, CancellationToken cancellationToken)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process, ownsHandle: false);
        using var waitHandle = new ManualResetEvent(false) { SafeWaitHandle = handle };

        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, _) => finished.TrySetResult(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);

        try
        {
            using (cancellationToken.Register(() => finished.TrySetCanceled(cancellationToken)))
            {
                await finished.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
