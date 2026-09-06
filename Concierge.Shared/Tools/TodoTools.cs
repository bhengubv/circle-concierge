using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared.Planning;

namespace Concierge.Shared.Tools;

/// <summary>
/// The working list, read.
///
/// `FileTodoStore` was registered in DI and nothing ever resolved it — a durable
/// list with nowhere to be written from and nobody to read it. This is the way in.
///
/// Why it is not the plan strip. The plan is what the assistant said it would do
/// in *this* turn, stated up front and ticked off as it goes; it is cleared when
/// the turn ends, because a checklist for finished work is clutter. This survives
/// turns, and survives the app closing. They answer different questions — "what is
/// happening right now" and "what is still outstanding" — and a job handed over on
/// Friday needs the second one.
///
/// Read-only, so it never interrupts. Looking at your own list is not an act.
/// </summary>
public sealed class TodoReadTool : IAgentTool
{
    private readonly ITodoStore _todos;

    public TodoReadTool(ITodoStore todos)
        => _todos = todos ?? throw new ArgumentNullException(nameof(todos));

    public string Name => "todo_read";

    public string Description =>
        "Read the working list of tasks that outlives this turn.";

    public bool IsReadOnly => true;

    public JsonNode? ArgumentsSchema => null;

    public Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var items = _todos.Read();

        if (items.Count == 0)
        {
            // "Nothing outstanding" and "the list failed to load" must not look the
            // same, or a model will invent work to fill a silence.
            return Task.FromResult(new AgentToolResult(true, "The list is empty.", null));
        }

        return Task.FromResult(new AgentToolResult(true, Describe(items), null));
    }

    internal static string Describe(IReadOnlyList<TodoItem> items)
    {
        var text = new StringBuilder();

        foreach (var item in items)
        {
            var mark = item.Status switch
            {
                TodoStatus.Completed => "[x]",
                TodoStatus.InProgress => "[~]",
                _ => "[ ]",
            };

            text.AppendLine($"{mark} {item.Content}");
        }

        var left = items.Count(i => i.Status != TodoStatus.Completed);

        text.AppendLine();
        text.Append(left == 0
            ? "Everything on the list is done."
            : left == 1 ? "1 still to do." : $"{left} still to do.");

        return text.ToString();
    }
}

/// <summary>
/// The working list, replaced.
///
/// Whole-list writes rather than per-item edits, which is the store's own design
/// and the right one: a model that has to name an item to change it will
/// eventually name one that has moved, and merging two views of a list is a
/// problem nobody needed to have.
///
/// Not read-only, and it does not ask. That combination is deliberate and worth
/// stating, because everything else in this file's neighbourhood that changes
/// something asks first. What this changes is the assistant's own notes — not a
/// file, not the device, nothing anybody else can see. Interrupting a person to
/// approve a to-do list is the prompt that teaches them to stop reading prompts,
/// and there is nothing here to protect them from.
/// </summary>
public sealed class TodoWriteTool : IAgentTool
{
    /// <summary>
    /// How many entries a list may hold.
    ///
    /// A model asked to plan will occasionally produce sixty. A list that long is
    /// not a plan, and it costs a slice of every later request in the conversation.
    /// </summary>
    public const int MaxItems = 24;

    private readonly ITodoStore _todos;

    public TodoWriteTool(ITodoStore todos)
        => _todos = todos ?? throw new ArgumentNullException(nameof(todos));

    public string Name => "todo_write";

    public string Description =>
        "Replace the working list of tasks. Send the whole list every time, including what is already done.";

    public bool IsReadOnly => false;

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "description": "The whole list, in order.",
              "items": {
                "type": "object",
                "properties": {
                  "content": { "type": "string", "description": "The task, as a short imperative line." },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                },
                "required": ["content"]
              }
            }
          },
          "required": ["items"] }
        """);

    public Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        if (arguments?["items"] is not JsonArray raw)
        {
            return Task.FromResult(new AgentToolResult(
                false, string.Empty, "Argument 'items' is required, as a list."));
        }

        var items = new List<TodoItem>();

        foreach (var entry in raw)
        {
            var content = entry?["content"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(content))
            {
                // A blank line in a to-do list is noise that survives every later
                // read of it. Skipped rather than refused: the rest of the list is
                // still worth keeping.
                continue;
            }

            items.Add(new TodoItem(content, StatusOf(entry?["status"]?.GetValue<string>())));

            if (items.Count == MaxItems)
            {
                break;
            }
        }

        _todos.Write(items);

        var left = items.Count(i => i.Status != TodoStatus.Completed);

        return Task.FromResult(new AgentToolResult(
            true,
            items.Count == 0
                ? "The list is now empty."
                : $"{items.Count} on the list, {left} still to do.",
            null));
    }

    /// <summary>
    /// Anything unrecognised is pending. A status nobody planned for should leave
    /// work on the list rather than quietly mark it finished.
    /// </summary>
    private static TodoStatus StatusOf(string? status) => status?.ToLowerInvariant() switch
    {
        "completed" or "done" or "complete" => TodoStatus.Completed,
        "in_progress" or "in-progress" or "active" or "doing" => TodoStatus.InProgress,
        _ => TodoStatus.Pending,
    };
}
