using System.Text.Json.Nodes;

namespace Concierge.Shared.Tools;

/// <summary>Read-only capability discovery; enumerates the live registry, including connected sources.</summary>
public sealed class DescribeToolsTool(Func<IReadOnlyList<IAgentTool>> catalogue) : IAgentTool
{
    public string Name => "describe_tools";
    public string Description => "Get complete descriptions and argument schemas for up to eight named tools.";
    public bool IsReadOnly => true;
    public JsonNode ArgumentsSchema => JsonNode.Parse("""
        {"type":"object","properties":{"names":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":8}},"required":["names"],"additionalProperties":false}
        """)!;
    public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments is not JsonObject args || args["names"] is not JsonArray names || names.Count is < 1 or > 8)
            return Task.FromResult(new AgentToolResult(false, "", "Supply one to eight exact tool names in names."));
        var tools = catalogue();
        var result = new JsonArray();
        foreach (var name in names)
        {
            if (name is not JsonValue value || !value.TryGetValue<string>(out var text))
                return Task.FromResult(new AgentToolResult(false, "", "Every name must be a string."));
            var tool = tools.FirstOrDefault(t => string.Equals(t.Name, text, StringComparison.OrdinalIgnoreCase));
            if (tool is null) return Task.FromResult(new AgentToolResult(false, "", $"Unknown tool: {text}. Use a name from the available tools list."));
            result.Add(new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description,
                ["readOnly"] = tool.IsReadOnly, ["argumentsSchema"] = tool.ArgumentsSchema?.DeepClone() ?? new JsonObject() });
        }
        return Task.FromResult(new AgentToolResult(true, result.ToJsonString()));
    }
}
