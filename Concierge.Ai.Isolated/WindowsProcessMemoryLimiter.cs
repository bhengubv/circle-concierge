using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Concierge.Ai.Isolated;

/// <summary>Applies a hard resident-memory ceiling to a Windows worker.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcessMemoryLimiter
{
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint HardWorkingSetMaximum = 0x00000004;
    private const long MinimumWorkingSetBytes = 1024 * 1024;

    internal static void Apply(Process process, long maximumWorkingSetBytes)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (maximumWorkingSetBytes < MinimumWorkingSetBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumWorkingSetBytes));

        var handle = OpenProcess(ProcessSetQuota, inheritHandle: false, process.Id);
        if (handle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not grant Concierge permission to cap the model worker's resident memory.");

        try
        {
            if (!SetProcessWorkingSetSizeEx(
                    handle,
                    (nuint)MinimumWorkingSetBytes,
                    (nuint)maximumWorkingSetBytes,
                    HardWorkingSetMaximum))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the model worker's resident-memory ceiling.");
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal static (nuint Minimum, nuint Maximum, uint Flags) Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var handle = OpenProcess(ProcessSetQuota | ProcessQueryInformation, inheritHandle: false, process.Id);
        if (handle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the model worker's memory limit.");

        try
        {
            if (!GetProcessWorkingSetSizeEx(handle, out var minimum, out var maximum, out var flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the model worker's memory limit.");
            return (minimum, maximum, flags);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(
        nint process,
        nuint minimumWorkingSetSize,
        nuint maximumWorkingSetSize,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessWorkingSetSizeEx(
        nint process,
        out nuint minimumWorkingSetSize,
        out nuint maximumWorkingSetSize,
        out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
