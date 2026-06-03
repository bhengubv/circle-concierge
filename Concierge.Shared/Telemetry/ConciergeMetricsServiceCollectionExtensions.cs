using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Telemetry;

public static class ConciergeMetricsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ConciergeMetrics"/> as a singleton so any service can pull counters
    /// without taking a transitive dependency on System.Diagnostics.Metrics directly.
    /// </summary>
    public static IServiceCollection AddConciergeMetrics(this IServiceCollection services)
    {
        services.TryAddSingleton<ConciergeMetrics>();
        return services;
    }
}
