using System.Runtime.InteropServices;
using System.Globalization;
using CircleAI.Core;

namespace Concierge.Ai;

/// <summary>
/// Supplies the model selector with current system-available RAM on Windows.
/// The core library's desktop heuristic uses total memory; that is not a safe
/// fit check when another workload (for example, WSL) is using most of it.
/// </summary>
internal static class WindowsAvailableMemoryProbe
{
    internal static DeviceProbe.PlatformMemory Read()
    {
        if (!OperatingSystem.IsWindows())
            return default;

        try
        {
            var status = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };

            if (!GlobalMemoryStatusEx(ref status))
                return default;

            var snapshot = FromBytes(status.AvailablePhysicalBytes, status.TotalPhysicalBytes);
            if (long.TryParse(
                    Environment.GetEnvironmentVariable("CONCIERGE_HOST_MEMORY_BUDGET_BYTES"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var workerBudget)
                && workerBudget > 0)
            {
                // The .NET Host Manager sends the same ceiling enforced by the
                // Windows job object. Circle AI therefore selects against the
                // worker's budget, not all RAM that happens to be in the PC.
                snapshot = LimitAvailableMemory(snapshot, workerBudget);
            }

            return snapshot;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // A failed optional probe must not prevent Concierge from starting.
            return default;
        }
    }

    internal static DeviceProbe.PlatformMemory FromBytes(ulong availableBytes, ulong totalBytes)
        => new(
            RamAvailableBytes: checked((long)availableBytes),
            StorageFreeBytes: null,
            RamTotalBytes: checked((long)totalBytes));

    internal static DeviceProbe.PlatformMemory LimitAvailableMemory(
        DeviceProbe.PlatformMemory snapshot, long workerBudgetBytes)
    {
        if (workerBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(workerBudgetBytes));

        long? available = snapshot.RamAvailableBytes is { } measured
            ? Math.Min(measured, workerBudgetBytes)
            : null;
        return new DeviceProbe.PlatformMemory(available, snapshot.StorageFreeBytes, snapshot.RamTotalBytes);
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
