using System.Text.Json.Nodes;

namespace Concierge.Shared.Tools;

/// <summary>
/// Adapter that exposes a single <see cref="IAgentHarnessService"/> operation as an
/// <see cref="IAgentTool"/>. The host registers one adapter per operation so the chat
/// runtime can iterate <c>IEnumerable&lt;IAgentTool&gt;</c> and present the catalogue
/// to the LLM without reflecting on the harness directly.
/// </summary>
public sealed class AgentHarnessReadTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;
    public AgentHarnessReadTool(IAgentHarnessService harness) => _harness = harness;

    public string Name => "read_file";
    public string Description => "Read a UTF-8 text file inside the trusted workspace.";
    public bool IsReadOnly => true;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative path." }
          },
          "required": ["path"] }
        """);

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments?["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AgentToolResult(false, string.Empty, "Argument 'path' is required.");
        }

        var result = await _harness.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        return ToAgentResult(result);
    }

    internal static AgentToolResult ToAgentResult(ConciergeToolResult result)
        => result.Outcome == ConciergeToolOutcome.Succeeded
            ? new AgentToolResult(true, result.Output, null)
            : new AgentToolResult(false, result.Output, result.Summary);
}

public sealed class AgentHarnessWriteTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;
    public AgentHarnessWriteTool(IAgentHarnessService harness) => _harness = harness;

    public string Name => "write_file";
    public string Description => "Create or replace a UTF-8 text file. Requires approval before execution.";
    public bool IsReadOnly => false;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative path." },
            "content": { "type": "string", "description": "New file content." }
          },
          "required": ["path", "content"] }
        """);

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments?["path"]?.GetValue<string>();
        var content = arguments?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path) || content is null)
        {
            return new AgentToolResult(false, string.Empty, "Arguments 'path' and 'content' are required.");
        }

        var result = await _harness.WriteFileAsync(path, content, approved: false, cancellationToken).ConfigureAwait(false);
        return AgentHarnessReadTool.ToAgentResult(result);
    }
}

public sealed class AgentHarnessRunTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;
    public AgentHarnessRunTool(IAgentHarnessService harness) => _harness = harness;

    public string Name => "run_command";
    public string Description => "Run an allowlisted shell command (no shell features). Requires approval for destructive commands.";
    public bool IsReadOnly => false;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "command": { "type": "string", "description": "Full command line (no pipes / chaining)." }
          },
          "required": ["command"] }
        """);

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var command = arguments?["command"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(command))
        {
            return new AgentToolResult(false, string.Empty, "Argument 'command' is required.");
        }

        var result = await _harness.RunCommandAsync(command, approved: false, cancellationToken).ConfigureAwait(false);
        return AgentHarnessReadTool.ToAgentResult(result);
    }
}
