using Aether.Dtn;
using Aether.Routing;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Mesh;

public static class ConciergeMeshServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the null mesh transport + run-log publisher with aether-protocol-backed
    /// implementations: a persistent Aether identity, an <see cref="IDtnService"/> backed
    /// by <see cref="InMemoryDtnBundleStore"/>, and a <see cref="DtnAgentRunLogPublisher"/>
    /// that wraps every finished agent run log into a DTN bundle.
    /// </summary>
    public static IServiceCollection AddConciergeMesh(this IServiceCollection services)
    {
        services.AddSingleton<AetherMeshTransportService>(sp => new AetherMeshTransportService(sp));
        services.RemoveAll<IMeshTransportService>();
        services.AddSingleton<IMeshTransportService>(sp => sp.GetRequiredService<AetherMeshTransportService>());

        services.AddSingleton<IMeshSender>(sp =>
            new NullMeshSender(sp.GetRequiredService<AetherMeshTransportService>().NodeTag.ToString()));
        services.AddSingleton<IDtnBundleStore, InMemoryDtnBundleStore>();
        services.AddSingleton<IDtnService>(sp => new DtnService(
            sp.GetRequiredService<IMeshSender>(),
            sp.GetRequiredService<IDtnBundleStore>()));

        services.RemoveAll<IAgentRunLogPublisher>();
        services.AddSingleton<IAgentRunLogPublisher>(sp => new DtnAgentRunLogPublisher(
            sp.GetRequiredService<IDtnService>(),
            sp.GetRequiredService<AetherMeshTransportService>().NodeTag.ToString()));

        return services;
    }
}
