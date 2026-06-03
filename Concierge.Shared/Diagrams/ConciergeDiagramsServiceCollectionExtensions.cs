using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Shared.Diagrams;

public static class ConciergeDiagramsServiceCollectionExtensions
{
    /// <summary>
    /// Registers every built-in diagram runtime. PenPot lives in a sibling adapter project
    /// (<c>Concierge.Diagrams.PenPot</c>) and registers itself via its own
    /// <c>AddConciergePenPotDiagrams</c> extension when the host wants Figma / PenPot import.
    /// </summary>
    public static IServiceCollection AddConciergeDiagrams(this IServiceCollection services)
    {
        services.AddSingleton<MermaidDiagramRuntime>();
        services.AddSingleton<IDiagramRuntime>(sp => sp.GetRequiredService<MermaidDiagramRuntime>());
        return services;
    }
}
