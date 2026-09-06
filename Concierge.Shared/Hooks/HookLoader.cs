using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Hooks;

/// <summary>What is registered, and anything wrong with the file, for the UI.</summary>
/// <param name="Hooks">The hooks that will run.</param>
/// <param name="Problem">Why some were skipped, or null.</param>
/// <param name="Path">Where the file is, so a person can go and edit it.</param>
public sealed record HookStatus(IReadOnlyList<HookRegistration> Hooks, string? Problem, string Path);

/// <summary>
/// Turning the hooks file into hooks that run.
/// </summary>
public static class ConciergeHooksServiceCollectionExtensions
{
    /// <summary>
    /// Registers the hook bridge, if and only if a person has written a hooks file.
    ///
    /// Read once at start-up rather than per call. A hook is a program that runs in
    /// front of tool calls, and a file re-read between one call and the next means
    /// the set of programs allowed to run can change halfway through a turn —
    /// which is exactly the kind of thing nobody wants to reason about afterwards.
    ///
    /// With no file, no bridge is registered at all and the scheduler behaves
    /// precisely as it did before. That is stronger than registering one with an
    /// empty list: nothing is constructed, nothing is asked, and the absence is
    /// visible in the container rather than hidden inside a no-op.
    /// </summary>
    public static IServiceCollection AddConciergeHooks(
        this IServiceCollection services, string? path = null)
    {
        var options = new HookOptions(path);
        var (hooks, problem) = options.Load();

        services.TryAddSingleton(new HookStatus(hooks, problem, options.Path));

        if (hooks.Count > 0)
        {
            services.TryAddSingleton<IHookBridge>(_ => new ProcessHookBridge(hooks));
        }

        return services;
    }
}
