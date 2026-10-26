using System.Text.Json;

namespace Concierge.Shared.Chat;

public sealed record FileActivity(Guid EventId, Guid? RunId, string Path, string Operation, string Outcome, DateTimeOffset At);

/// <summary>Projects recorded file calls and command results; never guesses at shell side effects.</summary>
public static class FileActivityProjection
{
    private static readonly Dictionary<string, string> Operations = new(StringComparer.Ordinal)
    {
        ["read_file"] = "Read", ["read_file_lines"] = "Read", ["read_notebook"] = "Read",
        ["write_file"] = "Write", ["edit_file"] = "Edit", ["edit_notebook"] = "Edit",
    };

    public static IReadOnlyList<FileActivity> From(IEnumerable<ConversationEvent> events)
    {
        var rows = new List<FileActivity>();
        var pending = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        Guid? runId = null;
        foreach (var entry in events.OrderBy(e => e.Seq))
        {
            if (entry.Type is ConversationEventType.TurnStart or ConversationEventType.TurnEnd)
            {
                pending.Clear(); runId = entry.Type == ConversationEventType.TurnStart ? entry.Id : null;
                continue;
            }
            try
            {
                if (entry.Type == ConversationEventType.ToolCall)
                {
                    var separator = entry.Data.IndexOf(' ');
                    if (separator < 1) continue;
                    var name = entry.Data[..separator];
                    var isCommand = string.Equals(name, "run_command", StringComparison.Ordinal);
                    if (!isCommand && !Operations.TryGetValue(name, out _)) continue;
                    var operation = isCommand ? "Command" : Operations[name];
                    using var args = JsonDocument.Parse(entry.Data[(separator + 1)..]);
                    var property = isCommand ? "command" : "path";
                    if (!args.RootElement.TryGetProperty(property, out var target) || target.ValueKind != JsonValueKind.String) continue;
                    var targetText = target.GetString();
                    if (string.IsNullOrWhiteSpace(targetText)) continue;
                    if (!pending.TryGetValue(name, out var queue)) pending[name] = queue = new();
                    queue.Enqueue(rows.Count);
                    rows.Add(new FileActivity(entry.Id, runId, targetText, operation, "No result recorded", entry.CreatedAt));
                }
                else if (entry.Type == ConversationEventType.ToolResult && entry.Data.StartsWith("```tool-result", StringComparison.Ordinal))
                {
                    var start = entry.Data.IndexOf('\n');
                    var end = entry.Data.LastIndexOf("```", StringComparison.Ordinal);
                    if (start < 0 || end <= start) continue;
                    using var json = JsonDocument.Parse(entry.Data[(start + 1)..end]);
                    if (!json.RootElement.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String
                        || !pending.TryGetValue(nameNode.GetString()!, out var queue) || queue.Count == 0) continue;
                    var index = queue.Dequeue();
                    var outcome = json.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                        ? "Succeeded" : "Failed or refused";
                    rows[index] = rows[index] with { Outcome = outcome };
                }
            }
            catch (JsonException) { /* An unreadable event cannot establish a completed file operation. */ }
        }
        return rows;
    }
}
