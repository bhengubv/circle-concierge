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
    /// Whether this platform lets one process start another and talk to it.
    ///
    /// False on Android (running a binary from the app's own data directory is
    /// blocked) and on iOS (child processes are forbidden outright). True
    /// everywhere else, which is every head that ships a model host beside the app.
    /// </summary>
    public static bool CanHostAChildProcess
        => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

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
        => services.AddConciergeAiIsolated(CanHostAChildProcess, options);

    /// <summary>
    /// The same thing, told outright whether this platform can host a child process.
    ///
    /// Exists so the rule can be checked on a machine that is not a phone. A test
    /// cannot become Android, and a guard nobody can exercise is a guard nobody
    /// knows the shape of — which is how the phone ended up with no model in the
    /// first place.
    /// </summary>
    public static IServiceCollection AddConciergeAiIsolated(
        this IServiceCollection services,
        bool childProcessesArePossible,
        IsolatedRuntimeOptions? options = null)
    {
        // Where a child process is not a thing that exists, this call does nothing
        // and the in-process runtime AddConciergeAi registered stays.
        //
        // **Without this the phone had no model at all**, which is not what anybody
        // decided. The native inference library ships inside the APK for arm and
        // arm64 and loads perfectly well; the isolation layer then deleted the only
        // runtime that could reach it and installed one that looks for an executable
        // beside the app. An APK has no executables beside the app, so every turn on
        // the handheld answered "The model host is missing from this install" — a
        // true sentence about a situation nobody chose.
        //
        // Android forbids running a binary out of the app's own data directory and
        // iOS forbids child processes outright, so this is a property of the
        // platform rather than of a deployment, and is decided as one. Probing for
        // the file instead would make a desktop install that happens to be
        // mid-copy silently fall back to the unisolated path, which is the opposite
        // of what the isolation is for.
        //
        // The trade is stated rather than hidden: on those two heads a native fault
        // costs the application, because there is no boundary available to put
        // between them. It is the trade every app on those platforms already makes,
        // and it beats a product that cannot answer.
        if (!childProcessesArePossible)
        {
            return services;
        }

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
