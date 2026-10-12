using System.Runtime.InteropServices;

namespace Concierge.Shared.Sandboxing;

/// <summary>Reads current physical-memory headroom from Windows.</summary>
public static class WindowsAvailableMemory
{
    public static long? MeasureAvailablePhysicalBytes()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status)
                ? checked((long)status.AvailablePhysicalBytes)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }
}
