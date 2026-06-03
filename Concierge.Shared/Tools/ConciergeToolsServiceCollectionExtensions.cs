using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Tools;

public static class ConciergeToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent-tool registry plus the harness-backed read/write/run tools so the
    /// chat flow can offer them to the LLM. Additional tools (MCP bridges, custom skills)
    /// register themselves the same way — anything implementing <see cref="IAgentTool"/>
    /// gets picked up by <see cref="AgentToolRegistry"/>.
    /// </summary>
    public static IServiceCollection AddConciergeTools(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        services.AddSingleton<IAgentTool, AgentHarnessReadTool>();
        services.AddSingleton<IAgentTool, AgentHarnessWriteTool>();
        services.AddSingleton<IAgentTool, AgentHarnessRunTool>();
        return services;
    }
}
