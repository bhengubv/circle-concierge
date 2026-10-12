using CircleAI.Core;

namespace Concierge.Tests;

internal static class CircleAiTestDefaults
{
    private const long TestRamBytes = 128_000_000_000;

    internal static DeviceProbe HighMemoryDevice { get; } = new(
        RamAvailableBytes: TestRamBytes,
        StorageFreeBytes: 2_000_000_000_000,
        Gpu: GpuKind.None,
        CpuCores: 16,
        Thermal: ThermalClass.Active,
        Connectivity: Connectivity.Online)
    {
        RamTotalBytes = TestRamBytes
    };

    internal static DeviceProbe Probe(string _) => HighMemoryDevice;
}
