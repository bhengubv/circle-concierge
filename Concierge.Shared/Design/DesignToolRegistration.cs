using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Design;

/// <summary>
/// Putting the canvas on the same agent loop as everything else.
/// </summary>
public static class DesignToolRegistration
{
    /// <summary>
    /// Registers the workbench and publishes the canvas as a tool source.
    ///
    /// The workbench is a singleton because there is one canvas on screen at a
    /// time and the tools have to find whichever it is. The source publishes
    /// nothing at all while no canvas is open, so a model in an ordinary
    /// conversation is never offered a tool for a surface that is not there.
    /// </summary>
    public static IServiceCollection AddConciergeDesignTools(this IServiceCollection services)
    {
        services.TryAddSingleton<DesignWorkbench>();

        // TryAddEnumerable and by type rather than by factory, for the same
        // reason the device capabilities are registered that way: it deduplicates
        // on the implementation type, a factory descriptor gives it nothing to
        // compare, and the throw lands during service composition where a MAUI
        // head shows no window and no error.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, DesignToolSource>());

        return services;
    }
}
