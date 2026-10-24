using System.Text.Json;

namespace Concierge.Shared.Chat;

public sealed record ConversationHistory(IReadOnlyList<ChatTurn> Turns, int ThroughSeq);

/// <summary>Shared model-history projection for reload and compaction. Source events remain immutable.</summary>
public static class ConversationHistoryProjection
{
    public static ConversationHistory Derive(string? systemPrompt, IReadOnlyList<ConversationEvent> events)
    {
        var ordered = events.OrderBy(e => e.Seq).ToArray();
        ConversationCompactionSnapshot? latest = null;
        foreach (var entry in ordered.Where(e => e.Type == ConversationEventType.CompactionReplace))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<ConversationCompactionSnapshot>(entry.Data);
                if (snapshot?.SchemaVersion == 1 && snapshot.ThroughSeq >= 0 && snapshot.ThroughSeq < entry.Seq
                    && snapshot.Turns is { Count: > 0 } && snapshot.Turns.All(t => t is not null
                        && t.Content is not null && t.Role is "system" or "user" or "assistant" or "tool")) latest = snapshot;
            }
            catch (JsonException) { } // Historical prose markers cannot erase any history.
        }
        var turns = latest?.Turns.Select(t => t with { Images = null }).ToList() ?? [];
        if (latest is null && !string.IsNullOrWhiteSpace(systemPrompt)) turns.Add(new("system", systemPrompt));
        foreach (var entry in ordered.Where(e => e.Seq > (latest?.ThroughSeq ?? -1)))
        {
            var role = entry.Type switch
            {
                ConversationEventType.UserMessage => "user",
                ConversationEventType.AssistantMessage when entry.ProducedBy != "Connection status" => "assistant",
                ConversationEventType.ToolResult => "tool",
                ConversationEventType.SystemMessage => "system",
                _ => null
            };
            if (role is not null) turns.Add(new(role, entry.Data));
        }
        return new(turns, ordered.LastOrDefault()?.Seq ?? -1);
    }
}
