using Concierge.Shared.Sandboxing;

namespace Concierge.Shared.Rpc.Mcp;

/// <summary>Windows resource allowance for configured MCP child-process groups.</summary>
public sealed record McpWorkerResourceBudget(int MaximumWorkers, long MemoryBytesPerWorker)
{
    public const int AbsoluteWorkerLimit = 4;
    public const long MemoryBytesPerWorkerLimit = 512L * 1024 * 1024;
    public const long ConciergeAndSystemReserveBytes = 1536L * 1024 * 1024;

    public bool CanStartAnother(int runningWorkers) => runningWorkers < MaximumWorkers;

    /// <summary>
    /// Derive bounded MCP concurrency from current free physical RAM. Preserve a
    /// 1.5 GiB reserve for Windows and Concierge, and cap the total number of
    /// independently configured worker groups at four.
    /// </summary>
    public static McpWorkerResourceBudget FromAvailableMemory(long availableBytes)
    {
        if (availableBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(availableBytes));

        var headroom = availableBytes - ConciergeAndSystemReserveBytes;
        var count = headroom <= 0
            ? 0
            : (int)Math.Min(AbsoluteWorkerLimit, headroom / MemoryBytesPerWorkerLimit);

        return new McpWorkerResourceBudget(count, MemoryBytesPerWorkerLimit);
    }

    /// <summary>Returns a fresh Windows free-memory measurement, or null if unavailable.</summary>
    public static long? MeasureAvailableMemoryBytes()
        => WindowsAvailableMemory.MeasureAvailablePhysicalBytes();
}
