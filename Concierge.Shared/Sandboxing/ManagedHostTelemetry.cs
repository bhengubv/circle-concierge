namespace Concierge.Shared.Sandboxing;

/// <summary>A point-in-time resource measurement for one Concierge-owned worker.</summary>
public sealed record ManagedHostSnapshot(
    int ProcessId,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    TimeSpan CpuTime,
    long? MemoryBudgetBytes,
    DateTimeOffset ObservedAt,
    long? ResidentMemoryBudgetBytes = null);

/// <summary>A named worker entry in a host that can own multiple child processes.</summary>
public sealed record ManagedHostMeasurement(
    string HostId,
    string DisplayName,
    ManagedHostSnapshot? Snapshot,
    string State,
    InferenceMeasurement? Inference = null);

/// <summary>Latest inference timing and sizes. Contains no prompt or answer text.</summary>
public sealed record InferenceMeasurement(
    int RequestId,
    int MessageCount,
    long InputCharacters,
    long SystemCharacters,
    DateTimeOffset StartedAt,
    double ElapsedMilliseconds,
    double? FirstChunkMilliseconds,
    long OutputCharacters,
    string State);

/// <summary>Describes a worker whose live lifecycle can be observed by the Host Manager.</summary>
public interface IManagedHostTelemetrySource
{
    /// <summary>Stable identifier used to correlate diagnostics across refreshes.</summary>
    string HostId { get; }

    /// <summary>Person-facing role for this worker.</summary>
    string DisplayName { get; }

    /// <summary>Latest measurement, or null when this worker is not running.</summary>
    ManagedHostSnapshot? CurrentSnapshot { get; }

    /// <summary>Optional latest inference diagnostics; sizes and timings only.</summary>
    InferenceMeasurement? LatestInference => null;
}

/// <summary>Telemetry for a managed service that owns several worker processes.</summary>
public interface IManagedHostCollectionTelemetrySource
{
    /// <summary>Current worker entries, including configured workers that are stopped.</summary>
    IReadOnlyList<ManagedHostMeasurement> CurrentHosts { get; }
}
