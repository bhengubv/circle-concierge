using Aether.Media.Core;
using Concierge.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Media;

public static class ConciergeMediaServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the null-object <see cref="IMediaStudioService"/> registered by
    /// <c>AddConciergeCore</c> with the aether-media-backed implementation.
    /// </summary>
    public static IServiceCollection AddConciergeMedia(this IServiceCollection services)
    {
        services.TryAddSingleton<IMediaLibrary, InMemoryMediaLibrary>();
        services.RemoveAll<IMediaStudioService>();
        services.AddSingleton<IMediaStudioService, AetherMediaStudioService>();
        return services;
    }
}
