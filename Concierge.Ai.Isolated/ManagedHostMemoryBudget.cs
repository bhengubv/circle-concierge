using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Diagnostics;

namespace Concierge.Ai.Isolated;

/// <summary>
/// A conservative per-worker ceiling derived from the memory available when the
/// host starts. The same value is passed to Circle AI's selector and the OS
/// process supervisor, so selection cannot offer a model larger than the host's
/// enforced budget.
/// </summary>
internal sealed record ManagedHostMemoryBudget(long WorkerLimitBytes)
{
    private const double AvailableFraction = 0.80;
    private const double PhysicalFraction = 0.60;
    private const long MinimumUsefulBudgetBytes = 512L * 1024 * 1024;

    internal static ManagedHostMemoryBudget? FromSnapshot(long availableBytes, long totalPhysicalBytes, long ownedWorkingSetBytes = 0)
    {
        if (availableBytes < 0 || totalPhysicalBytes <= 0 || ownedWorkingSetBytes < 0)
            return null;

        // Available RAM already excludes this worker. Its absolute cap must
        // include its existing resident allocation, or each load/prefill shrinks
        // its own budget and forces paging without any external memory pressure.
        var availableCeiling = (long)(((double)availableBytes + ownedWorkingSetBytes) * AvailableFraction);
        var physicalCeiling = (long)(totalPhysicalBytes * PhysicalFraction);
        var ceiling = Math.Min(availableCeiling, physicalCeiling);
        return ceiling >= MinimumUsefulBudgetBytes ? new ManagedHostMemoryBudget(ceiling) : null;
    }

    /// <summary>
    /// A running worker may keep its startup commit ceiling, but its resident set must
    /// tighten when current free memory falls. It never grows beyond the startup ceiling.
    /// </summary>
    internal static long TightenResidentLimit(long startupLimitBytes, long liveLimitBytes)
    {
        if (startupLimitBytes < MinimumUsefulBudgetBytes)
            throw new ArgumentOutOfRangeException(nameof(startupLimitBytes));
        if (liveLimitBytes < MinimumUsefulBudgetBytes)
            throw new ArgumentOutOfRangeException(nameof(liveLimitBytes));
        return Math.Min(startupLimitBytes, liveLimitBytes);
    }

    /// <summary>
    /// Applies a smaller live resident ceiling and reports it only after the OS
    /// accepted it. A failed application therefore cannot be surfaced as enforced.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static long TightenAndApplyResidentLimit(
        long currentLimitBytes,
        long startupLimitBytes,
        long liveLimitBytes,
        Process worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var candidate = TightenResidentLimit(startupLimitBytes, liveLimitBytes);
        if (candidate >= currentLimitBytes)
            return currentLimitBytes;

        WindowsProcessMemoryLimiter.Apply(worker, candidate);
        return candidate;
    }

    internal static ManagedHostMemoryBudget? MeasureWindows(Process? ownedWorker = null)
    {
        long ownedWorkingSet = 0;
        if (ownedWorker is not null)
        {
            try
            {
                ownedWorker.Refresh();
                if (ownedWorker.HasExited) return null;
                ownedWorkingSet = ownedWorker.WorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
        var memory = MeasureWindowsSnapshot();
        return memory is { } snapshot
            ? FromSnapshot(snapshot.AvailableBytes, snapshot.TotalPhysicalBytes, ownedWorkingSet)
            : null;
    }

    internal static WindowsPhysicalMemorySnapshot? MeasureWindowsSnapshot()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status)
                ? new WindowsPhysicalMemorySnapshot(
                    checked((long)status.AvailablePhysicalBytes),
                    checked((long)status.TotalPhysicalBytes))
                : null;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
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

internal readonly record struct WindowsPhysicalMemorySnapshot(
    long AvailableBytes,
    long TotalPhysicalBytes);
