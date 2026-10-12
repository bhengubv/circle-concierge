using CircleAI.Core;
using Concierge.Ai;
using Concierge.Ai.Isolated;
using Concierge.Shared.Sandboxing;
using System.Diagnostics;

namespace Concierge.Tests;

public sealed class ManagedHostManagerTests
{
    [Fact]
    public void Worker_allocation_does_not_reduce_its_own_resident_budget()
    {
        const long gib = 1024L * 1024 * 1024;
        var startup = ManagedHostMemoryBudget.FromSnapshot(20 * gib, 32 * gib)!;
        var loaded = ManagedHostMemoryBudget.FromSnapshot(12 * gib, 32 * gib, 8 * gib)!;
        Assert.Equal(startup.WorkerLimitBytes, loaded.WorkerLimitBytes);

        // A separate application using 6 GiB is real pressure and still reduces the cap.
        var externalPressure = ManagedHostMemoryBudget.FromSnapshot(6 * gib, 32 * gib, 8 * gib)!;
        Assert.Equal((long)(14 * gib * 0.80), externalPressure.WorkerLimitBytes);
        Assert.True(externalPressure.WorkerLimitBytes < startup.WorkerLimitBytes);
    }

    [Fact]
    public void Windows_worker_budget_is_absent_or_above_its_safe_minimum()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var budget = ManagedHostMemoryBudget.MeasureWindows();

        // Low-memory Windows sessions must refuse to start a worker rather
        // than making this environment-dependent smoke test fail.
        Assert.True(budget is null || budget.WorkerLimitBytes >= 512L * 1024 * 1024);
    }

    [Fact]
    public void Worker_memory_budget_reserves_headroom_from_available_and_physical_memory()
    {
        var available = 16L * 1024 * 1024 * 1024;
        var physical = 32L * 1024 * 1024 * 1024;

        var budget = ManagedHostMemoryBudget.FromSnapshot(available, physical);

        Assert.NotNull(budget);
        Assert.Equal((long)(available * 0.80), budget.WorkerLimitBytes);
    }

    [Fact]
    public void Resident_worker_limit_tightens_to_live_resources_without_exceeding_startup_ceiling()
    {
        const long startup = 16L * 1024 * 1024 * 1024;
        const long live = 9L * 1024 * 1024 * 1024;
        const long recovered = 24L * 1024 * 1024 * 1024;

        Assert.Equal(live, ManagedHostMemoryBudget.TightenResidentLimit(startup, live));
        Assert.Equal(startup, ManagedHostMemoryBudget.TightenResidentLimit(startup, recovered));
    }

    [Fact]
    public void Resident_limit_is_reported_only_after_a_smaller_limit_is_applied()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const long current = 1024L * 1024 * 1024;
        const long live = 768L * 1024 * 1024;
        using var child = Process.Start(new ProcessStartInfo(
            "powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 30")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(child);
        try
        {
            WindowsProcessMemoryLimiter.Apply(child, current);

            var enforced = ManagedHostMemoryBudget.TightenAndApplyResidentLimit(
                current, current, live, child);

            Assert.Equal(live, enforced);
            Assert.Equal((nuint)live, WindowsProcessMemoryLimiter.Read(child).Maximum);
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            child.WaitForExit(5000);
        }
    }

    [Fact]
    public void Worker_resident_limit_never_expands_after_resources_recover()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const long startup = 16L * 1024 * 1024 * 1024;
        const long recovered = 24L * 1024 * 1024 * 1024;
        using var child = Process.GetCurrentProcess();

        Assert.Equal(startup, ManagedHostMemoryBudget.TightenAndApplyResidentLimit(
            startup, startup, recovered, child));
    }

    [Theory]
    [InlineData(0, 8L * 1024 * 1024 * 1024)]
    [InlineData(8L * 1024 * 1024 * 1024, 0)]
    [InlineData(500L * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    public void Worker_budget_is_unavailable_when_a_safe_worker_cannot_be_started(long available, long total)
        => Assert.Null(ManagedHostMemoryBudget.FromSnapshot(available, total));

    [Fact]
    public void CircleAi_resource_probe_never_offers_more_memory_than_the_managed_worker_budget()
    {
        var actual = new DeviceProbe.PlatformMemory(
            18L * 1024 * 1024 * 1024,
            90L * 1024 * 1024 * 1024,
            32L * 1024 * 1024 * 1024);

        var bounded = WindowsAvailableMemoryProbe.LimitAvailableMemory(
            actual, 12L * 1024 * 1024 * 1024);

        Assert.Equal(12L * 1024 * 1024 * 1024, bounded.RamAvailableBytes);
        Assert.Equal(actual.RamTotalBytes, bounded.RamTotalBytes);
        Assert.Equal(actual.StorageFreeBytes, bounded.StorageFreeBytes);
    }

    [Fact]
    public void Windows_job_closes_its_managed_child_when_the_manager_releases_it()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var manager = new WindowsJobObjectSandbox(
            memoryCapBytes: 256L * 1024 * 1024,
            processCap: 1);
        manager.OpenJob();

        using var child = Process.Start(new ProcessStartInfo(
            "powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 30")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(child);
        manager.Confine(child);
        const long maximumWorkingSet = 256L * 1024 * 1024;
        WindowsProcessMemoryLimiter.Apply(child, maximumWorkingSet);
        var applied = WindowsProcessMemoryLimiter.Read(child);
        Assert.Equal((nuint)maximumWorkingSet, applied.Maximum);
        Assert.Equal(0x00000004u, applied.Flags & 0x00000004u);

        manager.Dispose();

        Assert.True(child.WaitForExit(5000), "Closing the Host Manager job must not leave a .NET worker orphaned.");
    }
}
