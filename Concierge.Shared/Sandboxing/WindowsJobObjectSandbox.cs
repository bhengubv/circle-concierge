using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Concierge.Shared.Sandboxing;

/// <summary>
/// Confines a child process with a Windows job object: it dies with the app, cannot spawn
/// escapees, and is capped on memory.
/// </summary>
/// <remarks>
/// <para>
/// The guarantee that matters most is <c>KILL_ON_JOB_CLOSE</c>. Without it a model-written
/// program that starts something and exits leaves that something running after Concierge is
/// gone — the class of bug nobody finds until a user's phone is warm in their pocket.
/// </para>
/// <para>
/// Honest about what this is not: a job object limits and terminates, it does not restrict
/// which files a process may open. That is why the strength here is
/// <see cref="SandboxStrength.Process"/> and not <see cref="SandboxStrength.Confined"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobObjectSandbox : ICodeSandbox, IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const int LimitKillOnJobClose = 0x2000;
    private const int LimitProcessMemory = 0x0100;
    private const int LimitActiveProcess = 0x0008;

    /// <summary>Memory a program may use before Windows stops it.</summary>
    private const long MemoryCapBytes = 512L * 1024 * 1024;

    /// <summary>How many processes the program may have at once, itself included.</summary>
    private const int ProcessCap = 4;

    private nint _job;

    /// <inheritdoc />
    public SandboxCapability Capability { get; } = new(
        SandboxStrength.Process,
        "windows-job-object",
        "The program is capped on memory and processes and dies with the app, but Windows job "
        + "objects do not restrict which files it may open.");

    /// <inheritdoc />
    public void Prepare(ProcessStartInfo startInfo, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        startInfo.WorkingDirectory = workspaceRoot;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
    }

    /// <inheritdoc />
    public void Confine(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        _job = CreateJobObject(nint.Zero, null);
        if (_job == nint.Zero)
        {
            throw new InvalidOperationException("A job object could not be created, so the program was not run.");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = LimitKillOnJobClose | LimitProcessMemory | LimitActiveProcess,
                ActiveProcessLimit = ProcessCap,
            },
            ProcessMemoryLimit = (nuint)MemoryCapBytes,
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_job, ExtendedLimitInformation, buffer, (uint)size))
            {
                throw new InvalidOperationException("The job object limits could not be set, so the program was not run.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (!AssignProcessToJobObject(_job, process.Handle))
        {
            throw new InvalidOperationException("The program could not be confined, so it was not run.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_job != nint.Zero)
        {
            // Closing the handle is what kills anything still inside it.
            CloseHandle(_job);
            _job = nint.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public int LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public int ActiveProcessLimit;
        public nuint Affinity;
        public int PriorityClass;
        public int SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
