using CircleAI.ContentPolicy;
using Concierge.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Safety;

public static class SafetyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Concierge parental-controls + content-filter pipeline.
    /// Wraps the registered <see cref="IChatRuntime"/> with the safety
    /// decorator so every chat call routes through the filter when Family
    /// Mode is on. Off-mode pays no per-call overhead — the decorator
    /// short-circuits to the inner runtime.
    /// </summary>
    /// <remarks>
    /// The host must register <see cref="ConciergeSafetySettings"/> as a
    /// singleton BEFORE calling this method so the Settings UI can mutate
    /// it and the filter sees the changes in real time. The default
    /// settings are KidMode = false / Strictness = Off, which means the
    /// decorator is a pass-through until the parent opts in.
    /// </remarks>
    public static IServiceCollection AddConciergeSafety(this IServiceCollection services)
    {
        services.TryAddSingleton<ConciergeSafetySettings>();
        services.TryAddSingleton<IContentFilter>(sp => new ConciergeContentFilter(
            () => sp.GetRequiredService<ConciergeSafetySettings>().Strictness));
        services.TryAddSingleton<IRefusalPolicy, ConciergeRefusalPolicy>();
        services.TryAddSingleton<ISafetyAuditLog>(_ => new JsonSafetyAuditLog());

        // Decorate the existing IChatRuntime — find it, wrap it, replace it.
        // We do this after the host has registered its chat runtime; calling
        // AddConciergeSafety() before AddConciergeAi() / AddConciergeChatCloud()
        // would no-op the decoration. Document at the call site.
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IChatRuntime));
        if (existing is not null)
        {
            services.Remove(existing);
            services.AddSingleton<IChatRuntime>(sp =>
            {
                IChatRuntime inner;
                if (existing.ImplementationFactory is not null)
                {
                    inner = (IChatRuntime)existing.ImplementationFactory(sp);
                }
                else if (existing.ImplementationInstance is IChatRuntime instance)
                {
                    inner = instance;
                }
                else if (existing.ImplementationType is not null)
                {
                    inner = (IChatRuntime)ActivatorUtilities.CreateInstance(sp, existing.ImplementationType);
                }
                else
                {
                    throw new InvalidOperationException(
                        "IChatRuntime registration shape was not recognised; cannot wrap with safety decorator.");
                }

                return new ContentFilterChatRuntimeDecorator(
                    inner,
                    sp.GetRequiredService<IContentFilter>(),
                    sp.GetRequiredService<IRefusalPolicy>(),
                    sp.GetRequiredService<ISafetyAuditLog>(),
                    () => sp.GetRequiredService<ConciergeSafetySettings>());
            });
        }

        return services;
    }
}
