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

            var created = CreateProcess(
                null,
                commandLine,
                nint.Zero,
                nint.Zero,
                bInheritHandles: true,
                dwCreationFlags: CreateNoWindow | ExtendedStartupInfoPresent,
                lpEnvironment: nint.Zero,
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

            using var process = Process.GetProcessById(info.dwProcessId);

            var output = ReadAllAsync(outRead, cancellationToken);
            var error = ReadAllAsync(errRead, cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return new ConfinedResult(process.ExitCode, await output + await error);
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
        }
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
}
