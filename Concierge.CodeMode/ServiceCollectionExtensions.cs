using Concierge.Shared;
using Concierge.Shared.Sandboxing;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.CodeMode;

public static class ConciergeCodeModeServiceCollectionExtensions
{
    /// <summary>
    /// Registers code mode: the runtime that starts and confines the child process, and the
    /// <c>run_code</c> tool the model sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only for hosts that can start and confine a child process. The MAUI host does not call
    /// this — on Android a child runs under the app's own user id, so there is no boundary to
    /// put a model-written program behind, and shipping it anyway would be claiming a safety
    /// the platform cannot provide.
    /// </para>
    /// <para>
    /// If the platform turns out to offer nothing, the tool is not registered at all rather
    /// than registered and quietly unconfined. A model offered a tool that refuses every call
    /// is worse than one that was never offered it.
    /// </para>
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="hostDirectory">Where <c>Concierge.CodeMode.Host</c> was built.</param>
    /// <param name="workspaceRoot">Where programs run. Defaults to the host directory.</param>
    public static IServiceCollection AddConciergeCodeMode(
        this IServiceCollection services,
        string hostDirectory,
        string? workspaceRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostDirectory);

        var sandbox = CodeSandbox.ForCurrentPlatform();
        if (sandbox.Capability.Strength == SandboxStrength.None)
        {
            // Nothing to run programs behind. Say nothing to the model rather than offer a
            // tool that can only refuse.
            return services;
        }

        var hostPath = Path.Combine(hostDirectory, "Concierge.CodeMode.Host.dll");

        services.TryAddSingleton<ICodeRuntime>(provider => new ProcessCodeRuntime(
            provider.GetRequiredService<IAgentToolRegistry>(),
            CodeSandbox.ForCurrentPlatform(),
            hostPath,
            workspaceRoot ?? hostDirectory,
            acceptNoSandbox: false,
            provider.GetRequiredService<ConciergeToolLoopOptions>().ToolTimeout));

        services.AddSingleton<IAgentTool>(provider =>
            new RunCodeTool(provider.GetRequiredService<ICodeRuntime>()));

        return services;
    }
}
