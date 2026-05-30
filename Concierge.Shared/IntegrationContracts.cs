using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared;

public sealed record LlmRuntimeSnapshot(
    string Engine,
    string EngineVersion,
    string DeviceSummary,
    bool RegistryAvailable,
    IReadOnlyList<LlmModelDescriptor> ProbedModels,
    string Summary);

public sealed record LlmModelDescriptor(
    string Name,
    string Version,
    string Quantization,
    bool Available);

public interface ILlmRuntimeService
{
    LlmRuntimeSnapshot GetSnapshot();
}

public sealed class NullLlmRuntimeService : ILlmRuntimeService
{
    public LlmRuntimeSnapshot GetSnapshot() => new(
        Engine: "Null",
        EngineVersion: "0.0.0",
        DeviceSummary: "No on-device runtime is wired in this host.",
        RegistryAvailable: false,
        ProbedModels: [],
        Summary: "Wire Concierge.Ai (or another ILlmRuntimeService) to expose a real runtime.");
}

public sealed record MeshTransportSnapshot(
    string Engine,
    string EngineVersion,
    string NodeTag,
    int CachedRoutes,
    int KnownPeers,
    int PendingBundles,
    bool IdentityIsPersisted,
    bool IsActive,
    string Summary);

public interface IMeshTransportService
{
    MeshTransportSnapshot GetSnapshot();
}

public sealed class NullMeshTransportService : IMeshTransportService
{
    public MeshTransportSnapshot GetSnapshot() => new(
        Engine: "Null",
        EngineVersion: "0.0.0",
        NodeTag: "—",
        CachedRoutes: 0,
        KnownPeers: 0,
        PendingBundles: 0,
        IdentityIsPersisted: false,
        IsActive: false,
        Summary: "Wire Concierge.Mesh (or another IMeshTransportService) to expose a real mesh.");
}

/// <summary>
/// Publishes a finished agent run log to wherever the host wants it relayed — by default
/// nowhere (NullAgentRunLogPublisher). Concierge.Mesh ships a DTN-backed implementation
/// that serializes the log into a delay-tolerant bundle for opportunistic delivery.
/// </summary>
public interface IAgentRunLogPublisher
{
    Task PublishAsync(ConciergeRunLog log, CancellationToken cancellationToken = default);
}

public sealed class NullAgentRunLogPublisher : IAgentRunLogPublisher
{
    public Task PublishAsync(ConciergeRunLog log, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed record MediaStudioSnapshot(
    string Engine,
    string EngineVersion,
    int LibraryItemCount,
    IReadOnlyList<MediaStudioItem> RecentItems,
    IReadOnlyList<string> SupportedKinds,
    string Summary);

public sealed record MediaStudioItem(
    string Title,
    string Codec,
    string ContentType,
    string FormattedDuration,
    string CreatorTag,
    IReadOnlyList<string> Tags);

public interface IMediaStudioService
{
    MediaStudioSnapshot GetSnapshot();
}

public sealed class NullMediaStudioService : IMediaStudioService
{
    public MediaStudioSnapshot GetSnapshot() => new(
        Engine: "Null",
        EngineVersion: "0.0.0",
        LibraryItemCount: 0,
        RecentItems: [],
        SupportedKinds: [],
        Summary: "Wire Concierge.Media (or another IMediaStudioService) to expose a real library.");
}

public static class ConciergeIntegrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers null-object defaults for the three integration surfaces (LLM runtime,
    /// mesh transport, media studio). Adapter packages override these via TryAdd-aware
    /// extension methods (e.g. <c>services.AddConciergeAi()</c>).
    /// </summary>
    public static IServiceCollection AddConciergeIntegrationDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<ILlmRuntimeService, NullLlmRuntimeService>();
        services.TryAddSingleton<IMeshTransportService, NullMeshTransportService>();
        services.TryAddSingleton<IMediaStudioService, NullMediaStudioService>();
        services.TryAddSingleton<IAgentRunLogPublisher, NullAgentRunLogPublisher>();
        return services;
    }
}
