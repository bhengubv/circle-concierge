using Concierge.Ai;

namespace Concierge.Tests;

public sealed class WindowsAvailableMemoryProbeTests
{
    [Fact]
    public void Maps_available_and_total_bytes_into_Circle_AI_resource_snapshot()
    {
        var memory = WindowsAvailableMemoryProbe.FromBytes(2_000_000_000, 16_000_000_000);

        Assert.Equal(2_000_000_000, memory.RamAvailableBytes);
        Assert.Null(memory.StorageFreeBytes);
        Assert.Equal(16_000_000_000, memory.RamTotalBytes);
    }

    [Fact]
    public void Windows_probe_reads_live_system_memory_without_loading_a_model()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var memory = WindowsAvailableMemoryProbe.Read();

        Assert.True(memory.RamAvailableBytes > 0);
        Assert.True(memory.RamTotalBytes >= memory.RamAvailableBytes);
    }
}
