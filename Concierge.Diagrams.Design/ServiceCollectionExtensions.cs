using Concierge.Shared.Diagrams;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Diagrams.Design;

public static class ConciergeDesignDiagramsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PenPot runtime + its HTTP client. Caller supplies an <see cref="PenPotApiOptions"/>
    /// factory — the host typically reads it from configuration so the access token never appears
    /// in source.
    /// </summary>
    public static IServiceCollection AddConciergePenPotDiagrams(
        this IServiceCollection services,
        Func<IServiceProvider, PenPotApiOptions> optionsFactory)
    {
        services.AddSingleton<PenPotDiagramRuntime>();
        services.AddSingleton<IDiagramRuntime>(sp => sp.GetRequiredService<PenPotDiagramRuntime>());

        services.AddHttpClient<PenPotApiClient>((sp, client) =>
        {
            var options = optionsFactory(sp);
            client.BaseAddress = options.BaseAddress;
            if (!string.IsNullOrEmpty(options.AccessToken))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Token {options.AccessToken}");
            }
        });
        services.AddSingleton(optionsFactory);
        return services;
    }

    public static IServiceCollection AddConciergeFigmaDiagrams(
        this IServiceCollection services,
        Func<IServiceProvider, FigmaApiOptions> optionsFactory)
    {
        services.AddSingleton<FigmaDiagramRuntime>();
        services.AddSingleton<IDiagramRuntime>(sp => sp.GetRequiredService<FigmaDiagramRuntime>());

        services.AddHttpClient<FigmaApiClient>((sp, client) =>
        {
            var options = optionsFactory(sp);
            client.BaseAddress = options.BaseAddress;
            if (!string.IsNullOrEmpty(options.AccessToken))
            {
                client.DefaultRequestHeaders.Add("X-Figma-Token", options.AccessToken);
            }
        });
        services.AddSingleton(optionsFactory);
        return services;
    }
}
