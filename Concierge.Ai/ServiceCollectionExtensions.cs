using CircleAI.Core;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Ai;

public static class ConciergeAiServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the null-object <see cref="ILlmRuntimeService"/> registered by
    /// <c>AddConciergeCore</c> with the CircleAI-backed implementation, registers the
    /// CircleAI-backed <see cref="IChatRuntime"/>, and starts the on-device model loader
    /// as a hosted service. The host must have already called <c>AddConciergeCore</c>
    /// (or otherwise registered the integration defaults) before invoking this method.
    /// </summary>
    public static IServiceCollection AddConciergeAi(this IServiceCollection services)
        => services.AddConciergeAi(new CircleAiChatOptions());

    /// <inheritdoc cref="AddConciergeAi(IServiceCollection)"/>
    public static IServiceCollection AddConciergeAi(this IServiceCollection services, CircleAiChatOptions chatOptions)
    {
        ArgumentNullException.ThrowIfNull(chatOptions);

        services.TryAddSingleton<IDeviceContext>(_ => NullDeviceContext.Instance);
        services.RemoveAll<ILlmRuntimeService>();
        services.AddSingleton<ILlmRuntimeService, CircleAiLlmRuntimeService>();

        services.AddSingleton(chatOptions);
        services.AddSingleton<CircleAiChatRuntime>();
        services.RemoveAll<IChatRuntime>();
        services.AddSingleton<IChatRuntime>(sp => sp.GetRequiredService<CircleAiChatRuntime>());
        services.AddHostedService<CircleAiChatRuntimeLoader>();

        return services;
    }

    /// <summary>
    /// Registers speech on the device — whisper for listening, onnx for speaking — behind the
    /// same <see cref="IVoiceRuntime"/> seam a cloud provider sits behind.
    ///
    /// Always registered, never conditionally: the runtime itself reports what it can do, and
    /// it can do nothing until the model files are in its folder. A host that registered it
    /// only when the files happened to be there at start-up would silently have no voice for
    /// somebody who put them in afterwards, and no screen anywhere would say why.
    /// </summary>
    public static IServiceCollection AddConciergeLocalVoice(
        this IServiceCollection services, LocalVoiceOptions? options = null)
    {
        services.AddSingleton(options ?? new LocalVoiceOptions());
        services.AddSingleton<CircleAiVoiceRuntime>();
        services.AddSingleton<Concierge.Shared.Media.IVoiceRuntime>(
            sp => sp.GetRequiredService<CircleAiVoiceRuntime>());

        return services;
    }
}
