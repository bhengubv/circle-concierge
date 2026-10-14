using Concierge.Ai;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Ai;

public static class CircleAiSharedServiceChatRegistration
{
    public static IServiceCollection AddConciergeAiSharedServiceChat(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ImplementationType?.Name == "CircleAiChatRuntimeLoader").ToArray())
            services.Remove(descriptor);
        services.RemoveAll<CircleAiChatRuntime>();
        services.RemoveAll<IChatRuntime>();
        services.RemoveAll<ILlmRuntimeService>();
        services.AddSingleton<CircleAiSharedServiceChatRuntime>();
        services.AddSingleton<IChatRuntime>(sp => sp.GetRequiredService<CircleAiSharedServiceChatRuntime>());
        services.AddSingleton<ILlmRuntimeService>(sp => sp.GetRequiredService<CircleAiSharedServiceChatRuntime>());
        services.AddHostedService<CircleAiSharedServiceChatLoader>();
        return services;
    }
}
