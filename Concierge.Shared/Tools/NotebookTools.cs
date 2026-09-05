using System.Text.Json.Nodes;
using Concierge.Shared.Notebooks;

namespace Concierge.Shared.Tools;

/// <summary>
/// Reading a notebook as cells rather than as JSON.
///
/// read_file already opens one, and that is the problem rather than the
/// solution: a notebook with six plots in it is mostly base64, and spending a
/// context window on embedded PNGs to read a dozen lines of Python is how a
/// turn runs out of room before it starts.
///
/// Read-only, like read_file, and through the same harness — so the workspace
/// boundary is the one that already exists rather than a second one written
/// here that could disagree with it.
/// </summary>
public sealed class NotebookReadTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;

    public NotebookReadTool(IAgentHarnessService harness)
        => _harness = harness ?? throw new ArgumentNullException(nameof(harness));

    public string Name => "read_notebook";

    public string Description =>
        "Read a Jupyter notebook as numbered cells with their outputs, rather than as raw JSON.";

    public bool IsReadOnly => true;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative path to a .ipynb file." }
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

        var read = await _harness.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != ConciergeToolOutcome.Succeeded)
        {
            return AgentHarnessReadTool.ToAgentResult(read);
        }

        return new AgentToolResult(true, Notebook.Render(read.Output), null);
    }
}

/// <summary>
/// Changing one cell, and only that cell.
///
/// The reason this is not just write_file: a model editing a notebook through
/// write_file has to reproduce the entire document, so every output, execution
/// count and untouched cell comes back from memory. Notebooks are where that
/// goes worst, because the parts most easily lost — recorded outputs — are
/// often the only record that the code ever ran.
///
/// Approved like every other write, with the same diff, so the person deciding
/// sees the actual change rather than a description of it.
/// </summary>
public sealed class NotebookEditTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;
    private readonly IToolApprovalService _approval;

    public NotebookEditTool(IAgentHarnessService harness, IToolApprovalService approval)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
    }

    public string Name => "edit_notebook";

    public string Description =>
        "Replace, insert or delete one cell in a Jupyter notebook. Everything else is left as it is. "
        + "The user is asked before it is written.";

    public bool IsReadOnly => false;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative path to a .ipynb file." },
            "cell": { "type": "integer", "description": "Which cell, numbered from 0 as read_notebook shows them." },
            "edit": { "type": "string", "enum": ["replace", "insert", "delete"], "description": "Default replace." },
            "source": { "type": "string", "description": "The cell's new contents. Not needed for delete." },
            "cell_type": { "type": "string", "enum": ["code", "markdown"], "description": "For insert. Default code." }
          },
          "required": ["path", "cell"] }
        """);

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments?["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AgentToolResult(false, string.Empty, "Argument 'path' is required.");
        }

        if (arguments?["cell"] is not { } cellNode || !TryIndex(cellNode, out var index))
        {
            return new AgentToolResult(false, string.Empty, "Argument 'cell' is required, as a number.");
        }

        NotebookEditKind? kind = (arguments["edit"]?.GetValue<string>() ?? "replace").ToLowerInvariant() switch
        {
            "insert" => NotebookEditKind.Insert,
            "delete" => NotebookEditKind.Delete,
            "replace" => NotebookEditKind.Replace,
            _ => null,
        };

        if (kind is null)
        {
            return new AgentToolResult(false, string.Empty,
                "Argument 'edit' must be replace, insert or delete.");
        }

        var read = await _harness.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (read.Outcome != ConciergeToolOutcome.Succeeded)
        {
            return AgentHarnessReadTool.ToAgentResult(read);
        }

        var edited = Notebook.Edit(
            read.Output,
            index,
            kind.Value,
            arguments["source"]?.GetValue<string>(),
            arguments["cell_type"]?.GetValue<string>() ?? "code");

        if (!edited.Success)
        {
            // A cell that is not there, or a file that is not a notebook.
            // Nobody is interrupted for a write that was never going to happen.
            return new AgentToolResult(false, string.Empty, edited.Problem);
        }

        var preview = await _harness
            .PreviewWriteFileAsync(path, edited.Json!, cancellationToken).ConfigureAwait(false);

        var decision = await _approval.RequestAsync(
            new ToolApprovalRequest(
                Name,
                $"{Verb(kind.Value)} cell {index} in {path}",
                ConciergeToolRisk.High,
                preview.DiffPreview),
            cancellationToken).ConfigureAwait(false);

        var result = await _harness
            .WriteFileAsync(path, edited.Json!, approved: decision == ToolApprovalDecision.Allowed, cancellationToken)
            .ConfigureAwait(false);

        return AgentHarnessReadTool.ToAgentResult(result);
    }

    private static string Verb(NotebookEditKind kind) => kind switch
    {
        NotebookEditKind.Insert => "Insert a cell before",
        NotebookEditKind.Delete => "Delete",
        _ => "Replace",
    };

    /// <summary>
    /// A model asked for an integer sometimes sends "2". Refusing that buys
    /// nothing — the alternative is a failed turn over a pair of quotes.
    /// </summary>
    private static bool TryIndex(JsonNode node, out int index)
    {
        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            index = node.GetValue<int>();
            return true;
        }

        return int.TryParse(node.ToString(), out index);
    }
}
