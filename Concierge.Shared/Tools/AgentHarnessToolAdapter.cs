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

/// <summary>
/// Writes a file, but only after a person has approved this specific write. The approval
/// request carries the diff produced by <see cref="IAgentHarnessService.PreviewWriteFileAsync"/>
/// so the decision is made against the actual change, not the model's description of it.
/// </summary>
public sealed class AgentHarnessWriteTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;
    private readonly IToolApprovalService _approval;

    public AgentHarnessWriteTool(IAgentHarnessService harness, IToolApprovalService approval)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
    }

    public string Name => "write_file";
    public string Description => "Create or replace a UTF-8 text file. The user is asked before it is written.";
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

        // Preview first: the diff is what the person is actually approving, and building it
        // also surfaces a rejected path before anyone is interrupted.
        var preview = await _harness.PreviewWriteFileAsync(path, content, cancellationToken).ConfigureAwait(false);

        var decision = await _approval.RequestAsync(
            new ToolApprovalRequest(
                Name,
                $"Write {path}",
                ConciergeToolRisk.High,
                preview.DiffPreview),
            cancellationToken).ConfigureAwait(false);

        var result = await _harness
            .WriteFileAsync(path, content, approved: decision == ToolApprovalDecision.Allowed, cancellationToken)
            .ConfigureAwait(false);

        return AgentHarnessReadTool.ToAgentResult(result);
    }
}

/// <summary>
/// Runs an allowlisted command, asking a person first unless the command only reads state.
/// A command the harness would refuse outright is refused without interrupting anyone.
/// </summary>
public sealed class AgentHarnessRunTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;
    private readonly IToolApprovalService _approval;

    public AgentHarnessRunTool(IAgentHarnessService harness, IToolApprovalService approval)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
    }

    public string Name => "run_command";
    public string Description => "Run an allowlisted command (no shell features). The user is asked before anything that changes state.";
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

        // Try unattended first. The harness runs read-only commands without approval and
        // returns ApprovalRequired for everything else, so this both executes the safe case
        // and tells us whether asking is worthwhile — a command that is denied outright
        // (not allowlisted, chained, malformed) never reaches a person.
        var unattended = await _harness.RunCommandAsync(command, approved: false, cancellationToken).ConfigureAwait(false);
        if (unattended.Outcome != ConciergeToolOutcome.ApprovalRequired)
        {
            return AgentHarnessReadTool.ToAgentResult(unattended);
        }

        var decision = await _approval.RequestAsync(
            new ToolApprovalRequest(
                Name,
                "Run a command",
                ConciergeToolRisk.High,
                command),
            cancellationToken).ConfigureAwait(false);

        if (decision != ToolApprovalDecision.Allowed)
        {
            return AgentHarnessReadTool.ToAgentResult(unattended);
        }

        var approved = await _harness.RunCommandAsync(command, approved: true, cancellationToken).ConfigureAwait(false);
        return AgentHarnessReadTool.ToAgentResult(approved);
    }
}
