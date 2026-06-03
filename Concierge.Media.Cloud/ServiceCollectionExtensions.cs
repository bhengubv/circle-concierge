using Concierge.Shared.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Media.Cloud;

public static class ConciergeCloudMediaServiceCollectionExtensions
{
    public static IServiceCollection AddOpenAiImages(
        this IServiceCollection services,
        Func<IServiceProvider, OpenAiImageOptions> optionsFactory)
    {
        services.AddHttpClient<OpenAiImageRuntime>();
        services.AddSingleton(optionsFactory);
        services.AddSingleton<IImageRuntime>(sp => sp.GetRequiredService<OpenAiImageRuntime>());
        return services;
    }

    public static IServiceCollection AddStabilityImages(
        this IServiceCollection services,
        Func<IServiceProvider, StabilityImageOptions> optionsFactory)
    {
        services.AddHttpClient<StabilityImageRuntime>();
        services.AddSingleton(optionsFactory);
        services.AddSingleton<IImageRuntime>(sp => sp.GetRequiredService<StabilityImageRuntime>());
        return services;
    }

    public static IServiceCollection AddOpenAiVoice(
        this IServiceCollection services,
        Func<IServiceProvider, OpenAiVoiceOptions> optionsFactory)
    {
        services.AddHttpClient<OpenAiVoiceRuntime>();
        services.AddSingleton(optionsFactory);
        services.AddSingleton<IVoiceRuntime>(sp => sp.GetRequiredService<OpenAiVoiceRuntime>());
        return services;
    }

    /// <summary>
    /// Registers <see cref="NullImageRuntime"/> + <see cref="NullVoiceRuntime"/> so the UI
    /// always has at least one runtime to render in its selector (with a "needs key"
    /// status) even before any provider is wired.
    /// </summary>
    public static IServiceCollection AddConciergeMediaCloudDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<NullImageRuntime>();
        services.TryAddSingleton<NullVoiceRuntime>();
        return services;
    }
}
