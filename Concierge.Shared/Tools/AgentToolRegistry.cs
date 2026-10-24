using System.Text;
using System.Text.Json.Nodes;

namespace Concierge.Shared.Tools;

/// <summary>
/// In-process <see cref="IAgentToolRegistry"/>. Tools are registered as DI services and
/// collected here; the registry is the single source of truth the chat flow consults when
/// composing tool-aware prompts.
/// </summary>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyList<IAgentTool> _compiledIn;
    private readonly IReadOnlyList<IAgentToolSource> _sources;

    public AgentToolRegistry(
        IEnumerable<IAgentTool> tools,
        IEnumerable<IAgentToolSource>? sources = null)
    {
        _compiledIn = tools.ToList();
        _sources = sources?.ToList() ?? [];
    }

    /// <summary>
    /// Computed rather than stored, because not every tool exists at start-up.
    ///
    /// Tools registered in DI are known when this is built; tools published by
    /// an MCP server are not known until it has been started and asked, which
    /// happens after. Snapshotting here meant a connected server's tools could
    /// never reach the model — the catalogue had already been taken.
    ///
    /// Name collisions resolve first-wins, compiled-in before remote: a server
    /// must not be able to shadow read_file by publishing its own.
    /// </summary>
    public IReadOnlyList<IAgentTool> Tools =>
        _compiledIn
            .Concat(_sources.SelectMany(source => source.Tools))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string BuildSystemPromptAddendum()
    {
        if (Tools.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("You have the following tools available. To call one, emit a fenced block:");
        sb.AppendLine();
        sb.AppendLine("```tool-call");
        sb.AppendLine("{\"name\": \"<tool-name>\", \"arguments\": {<args>}}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("The host will execute the tool and reply with a ```tool-result block. Wait for that result before continuing.");
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        foreach (var tool in Tools)
        {
            var schema = tool.ArgumentsSchema?.ToJsonString() ?? "{}";
            var marker = tool.IsReadOnly ? "[read-only]" : "[requires approval]";
            sb.Append("- `").Append(tool.Name).Append("` ").Append(marker)
              .Append(" — ").AppendLine(tool.Description);
            sb.Append("  schema: ").AppendLine(schema);
        }

        return sb.ToString();
    }

    public string BuildDiscoveryPromptAddendum()
    {
        var snapshot = Tools;
        if (!snapshot.Any(t => t.Name == "describe_tools")) return BuildSystemPromptAddendum();
        var sb = new StringBuilder("Answer directly when no tool is needed. Before using a tool, request its description and schema:\n```tool-call\n{\"name\":\"describe_tools\",\"arguments\":{\"names\":[\"tool_name\"]}}\n```\nUse the same tool-call format to call it. Wait for tool-result; never invent success.\n");
        // Names keep the complete live catalogue discoverable; descriptions and schemas
        // arrive on demand. Repeating them on every ordinary question dominates prefill.
        foreach (var readOnly in new[] { true, false })
        {
            sb.Append(readOnly ? "[read-only]: " : "[requires approval]: ");
            sb.AppendLine(string.Join(", ", snapshot.Where(t => t.IsReadOnly == readOnly).Select(t => t.Name)));
        }
        return sb.ToString();
    }
}
