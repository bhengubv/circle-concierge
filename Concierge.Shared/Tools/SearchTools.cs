using System.Text.Json.Nodes;

namespace Concierge.Shared.Tools;

/// <summary>
/// Naming the files that are there.
///
/// The model was given read_file and no way to discover a path, so it could only open
/// something a person had already named to it. Every question that starts "where is…"
/// or "which file handles…" was unanswerable, and the Engineering room listed a
/// list_files tool for months with nothing behind it.
///
/// Read-only. It names files; it does not open them.
/// </summary>
public sealed class ListFilesTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;

    public ListFilesTool(IAgentHarnessService harness)
        => _harness = harness ?? throw new ArgumentNullException(nameof(harness));

    public string Name => "list_files";

    public string Description =>
        "List files in the workspace, optionally under a folder and matching a pattern like *.cs.";

    public bool IsReadOnly => true;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative folder. Default the whole workspace." },
            "pattern": { "type": "string", "description": "Filename pattern, e.g. *.cs. Default everything." }
          } }
        """);

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var result = await _harness.ListFilesAsync(
            arguments?["path"]?.GetValue<string>(),
            arguments?["pattern"]?.GetValue<string>(),
            cancellationToken).ConfigureAwait(false);

        return Described(result);
    }

    /// <summary>
    /// The count and the cap ride with the content. A list of exactly three hundred
    /// paths and a list of everything look identical, and a model that cannot tell
    /// them apart will conclude the fourth file does not exist.
    /// </summary>
    internal static AgentToolResult Described(ConciergeToolResult result)
        => result.Outcome == ConciergeToolOutcome.Succeeded
            ? new AgentToolResult(true, $"{result.Summary}\n\n{result.Output}".TrimEnd(), null)
            : new AgentToolResult(false, string.Empty, result.Summary);
}

/// <summary>
/// Finding a string across files.
///
/// The other half of not being able to look around. Without it the only way to find
/// where something is used is to read every file that might use it, which no context
/// window survives.
///
/// Plain text, not a regular expression — a model searching a repository is nearly
/// always looking for an identifier, and an accidental pattern in a symbol name either
/// matches nothing or matches everything, with no way to tell which from the result.
/// </summary>
public sealed class SearchTextTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;

    public SearchTextTool(IAgentHarnessService harness)
        => _harness = harness ?? throw new ArgumentNullException(nameof(harness));

    public string Name => "search_text";

    public string Description =>
        "Find a plain string across workspace files. Returns each match with its file and line number.";

    public bool IsReadOnly => true;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "query": { "type": "string", "description": "The text to find. Matched literally, not as a regex." },
            "path": { "type": "string", "description": "Workspace-relative folder to search under." },
            "pattern": { "type": "string", "description": "Only search files matching this, e.g. *.cs." }
          },
          "required": ["query"] }
        """);

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var query = arguments?["query"]?.GetValue<string>();
        if (string.IsNullOrEmpty(query))
        {
            return new AgentToolResult(false, string.Empty, "Argument 'query' is required.");
        }

        var result = await _harness.SearchTextAsync(
            query,
            arguments?["path"]?.GetValue<string>(),
            arguments?["pattern"]?.GetValue<string>(),
            cancellationToken).ConfigureAwait(false);

        return ListFilesTool.Described(result);
    }
}

/// <summary>
/// Reading part of a large file.
///
/// ReadFileWindowAsync has been on the harness the whole time and was never reachable
/// from a conversation, so the only way to look at a 4,000-line file was to load all
/// of it. Half the context window spent to check one method.
/// </summary>
public sealed class ReadFileWindowTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;

    public ReadFileWindowTool(IAgentHarnessService harness)
        => _harness = harness ?? throw new ArgumentNullException(nameof(harness));

    public string Name => "read_file_lines";

    public string Description =>
        "Read a range of lines from a file, for files too large to read whole.";

    public bool IsReadOnly => true;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative path." },
            "offset": { "type": "integer", "description": "First line, counting from 1. Default 1." },
            "limit": { "type": "integer", "description": "How many lines. Default 200." }
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

        // Lines are 1-based to a person and to every editor; the harness counts from 0.
        // Translating here rather than in the schema keeps the tool talking the way the
        // model was already going to write it.
        var offset = Math.Max(1, Number(arguments?["offset"], 1)) - 1;
        var limit = Math.Clamp(Number(arguments?["limit"], 200), 1, 2000);

        var result = await _harness
            .ReadFileWindowAsync(path, offset, limit, cancellationToken).ConfigureAwait(false);

        return ListFilesTool.Described(result);
    }

    private static int Number(JsonNode? node, int fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        return node.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? node.GetValue<int>()
            : int.TryParse(node.ToString(), out var parsed) ? parsed : fallback;
    }
}

/// <summary>
/// Changing part of a file instead of rewriting it.
///
/// EditFileAsync has also been on the harness the whole time, with a diff preview and
/// an approval contract, and was never exposed. write_file was the only way to change
/// anything, so altering one line meant the model reproducing the entire file from
/// memory — the same fault the notebook tools were built to avoid, sitting on every
/// other file in the repository, and the one most likely to lose work quietly.
///
/// The harness refuses text that appears more than once rather than guessing which
/// line was meant, and says how many times it appeared so the model can retry with
/// more surrounding context.
/// </summary>
public sealed class EditFileTool : IAgentTool
{
    private readonly IAgentHarnessService _harness;
    private readonly IToolApprovalService _approval;

    public EditFileTool(IAgentHarnessService harness, IToolApprovalService approval)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
    }

    public string Name => "edit_file";

    public string Description =>
        "Replace one exact piece of text in a file, leaving the rest untouched. "
        + "The user is asked before it is written.";

    public bool IsReadOnly => false;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Workspace-relative path." },
            "find": { "type": "string", "description": "Exact text to replace. Must appear once." },
            "replace": { "type": "string", "description": "What to put in its place." }
          },
          "required": ["path", "find", "replace"] }
        """);

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments?["path"]?.GetValue<string>();
        var find = arguments?["find"]?.GetValue<string>();
        var replace = arguments?["replace"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(find) || replace is null)
        {
            return new AgentToolResult(false, string.Empty, "Arguments 'path', 'find' and 'replace' are required.");
        }

        // Preview first, exactly as write_file does: the diff is what is being approved,
        // and building it surfaces a rejected path before anybody is interrupted.
        var preview = await _harness
            .PreviewEditFileAsync(path, find, replace, cancellationToken).ConfigureAwait(false);

        var decision = await _approval.RequestAsync(
            new ToolApprovalRequest(Name, $"Edit {path}", ConciergeToolRisk.High, preview.DiffPreview),
            cancellationToken).ConfigureAwait(false);

        var result = await _harness
            .EditFileAsync(path, find, replace, approved: decision == ToolApprovalDecision.Allowed, cancellationToken)
            .ConfigureAwait(false);

        return AgentHarnessReadTool.ToAgentResult(result);
    }
}
