using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Concierge.Shared.Chat;

namespace Concierge.Ai.Isolated;

/// <summary>Where the model host lives, and how long to wait for it.</summary>
public sealed class IsolatedRuntimeOptions
{
    /// <summary>
    /// Explicit path to the host executable. Left null, it is looked for beside
    /// the application, which is where the build puts it.
    /// </summary>
    public string? HostPath { get; set; }

    /// <summary>
    /// How long to wait for the child to load the model and say hello.
    ///
    /// Generous, because it is loading several hundred megabytes of weights
    /// from disk on first run, and a person who sees "not ready" because the
    /// timeout was tight will reasonably conclude the product is broken.
    /// </summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Finds the host, or null. Checked rather than assumed because a missing
    /// host is a deployment mistake that should say so plainly instead of
    /// looking like a model that will not load.
    /// </summary>
    public string? ResolveHostPath()
    {
        if (!string.IsNullOrWhiteSpace(HostPath))
        {
            return File.Exists(HostPath) ? HostPath : null;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { "Concierge.Model.Host.exe" }
            : new[] { "Concierge.Model.Host" };

        var directories = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "ModelHost"),
        };

        return directories
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }
}

public static class IsolatedAiServiceCollectionExtensions
{
    /// <summary>
    /// Runs the on-device model in a child process instead of this one.
    ///
    /// Use this INSTEAD of AddConciergeAi, not alongside it. Registering both
    /// would load the native library into this process, which is the thing
    /// being avoided — the isolation is only real if the parent never links
    /// the code that faults.
    ///
    /// The trade is a process boundary and a JSON line per chunk. In exchange,
    /// a model that faults mid-sentence costs you the sentence rather than the
    /// application, the conversation you were typing, and everything else open.
    /// </summary>
    public static IServiceCollection AddConciergeAiIsolated(
        this IServiceCollection services, IsolatedRuntimeOptions? options = null)
    {
        services.TryAddSingleton(options ?? new IsolatedRuntimeOptions());
        services.AddSingleton<IsolatedChatRuntime>();
        services.RemoveAll<IChatRuntime>();
        services.AddSingleton<IChatRuntime>(sp => sp.GetRequiredService<IsolatedChatRuntime>());

        // And stop this process loading the model itself.
        //
        // AddConciergeAi registers a hosted service that loads the weights at
        // start-up, plus the in-process runtime it loads them into. Leaving
        // either behind means the native library is mapped into Concierge after
        // all — measured at 770 MB resident with the child not even started —
        // and a library that is loaded is a library that can fault.
        //
        // Matched by name rather than by type because this project deliberately
        // does not reference the one those types live in. Referencing it to
        // remove it would defeat the point.
        // Started eagerly, like the runtime it replaces: the composer is
        // disabled until ready, so a child that only starts on first use means
        // a first message that can never be sent.
        services.AddHostedService<IsolatedRuntimeLoader>();

        RemoveByTypeName(services, "CircleAiChatRuntimeLoader");
        RemoveByTypeName(services, "CircleAiChatRuntime");

        return services;
    }

    private static void RemoveByTypeName(IServiceCollection services, string typeName)
    {
        var doomed = services
            .Where(d => d.ImplementationType?.Name == typeName
                     || d.ServiceType.Name == typeName)
            .ToList();

        foreach (var descriptor in doomed)
        {
            services.Remove(descriptor);
        }
    }
}
