namespace Concierge.Shared.Devices;

/// <summary>A fact returned by the shared service, not a client capability guess.</summary>
public sealed record CircleAiServiceCapability(string Id, string ImplementationStatus, string Summary);

/// <summary>
/// Installation and authorized catalogue discovery. The v1 Android link does not
/// report model readiness; a successful catalogue must never enable inference UI.
/// </summary>
public sealed record CircleAiSharedServiceSnapshot(
    string Package,
    string? PackageVersion,
    long? PackageBuild,
    bool Installed,
    bool Authorized,
    string Status,
    IReadOnlyList<CircleAiServiceCapability> Capabilities);

public interface ICircleAiSharedService
{
    /// <summary>Read-only, model-free discovery; never downloads or asks a model.</summary>
    Task<CircleAiSharedServiceSnapshot> InspectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Opens the service-owned OS approval sheet from the foreground client.</summary>
public interface ICircleAiSharedServiceLink
{
    Task<bool> RequestConnectionAsync(CancellationToken cancellationToken = default);
}
