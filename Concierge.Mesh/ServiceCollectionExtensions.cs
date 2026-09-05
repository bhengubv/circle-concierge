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
    /// <param name="onTheNetwork">
    /// Whether to attach the real radio. True gives <see cref="LanMeshSender"/>:
    /// peers found by multicast on the local network, packets delivered over TCP.
    /// False keeps <see cref="NullMeshSender"/>, which reports no peers and refuses
    /// every send — bundles are still created and still queue, they simply have
    /// nowhere to go yet.
    ///
    /// A parameter rather than always-on, because attaching a radio puts traffic on
    /// somebody's network and opens a port on their machine. That is a thing a host
    /// should decide out loud, not inherit from a package default.
    /// </param>
    public static IServiceCollection AddConciergeMesh(
        this IServiceCollection services, bool onTheNetwork = false)
    {
        services.AddSingleton<AetherMeshTransportService>(sp => new AetherMeshTransportService(sp));
        services.RemoveAll<IMeshTransportService>();
        services.AddSingleton<IMeshTransportService>(sp => sp.GetRequiredService<AetherMeshTransportService>());

        // The radio. Everything above this line — bundles, custody, opportunistic
        // delivery — was already here and had nothing to talk over.
        services.AddSingleton<IMeshSender>(sp =>
        {
            var uhid = sp.GetRequiredService<AetherMeshTransportService>().NodeTag.ToString();

            return onTheNetwork
                ? new LanMeshSender(uhid)
                : new NullMeshSender(uhid);
        });
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
