using System.Text.Json;
using Concierge.Shared.Chat;

namespace Concierge.Away;

public sealed record WristConversation(Guid Id, string Title, DateTimeOffset UpdatedAt, string Status, int LastMessageSeq = -1);
public sealed record WristMessage(int Seq, string Who, string Text, DateTimeOffset At);

/// <summary>Read-only watch projection. A missing receipt is not proof a run is active.</summary>
public static class WristTimeline
{
    public static string Status(IReadOnlyList<ConversationEvent> events, bool running)
    {
        if (running) return "Working";
        var last = events.Where(e => e.Type is ConversationEventType.TurnStart or ConversationEventType.TurnEnd)
            .OrderBy(e => e.Seq).LastOrDefault();
        if (last is null) return "Saved";
        if (last.Type == ConversationEventType.TurnStart) return "Interrupted";
        try
        {
            var receipt = JsonSerializer.Deserialize<TurnRunReceipt>(last.Data);
            if (receipt?.Text?.Contains("[stream error:", StringComparison.OrdinalIgnoreCase) == true) return "Needs retry";
            return receipt?.Status switch { "completed" => "Answered", "cancelled" => "Stopped", "failed" => "Needs retry", _ => "Status unavailable" };
        }
        catch (JsonException) { return "Status unavailable"; }
    }

    public static IReadOnlyList<WristMessage> Messages(IEnumerable<ConversationEvent> events)
        => events.OrderBy(e => e.Seq).Where(e => e.Type is ConversationEventType.UserMessage
            or ConversationEventType.AssistantMessage or ConversationEventType.ConnectionStatus
            or ConversationEventType.ApprovalAsked or ConversationEventType.ApprovalDecided)
            .Select(e => new WristMessage(e.Seq, e.Type switch
            {
                ConversationEventType.UserMessage => "You",
                ConversationEventType.AssistantMessage => "Concierge",
                ConversationEventType.ApprovalAsked => "Approval requested",
                ConversationEventType.ApprovalDecided => "Approval decision",
                _ => "Status"
            }, e.Type is ConversationEventType.ApprovalAsked or ConversationEventType.ApprovalDecided
                ? ApprovalText(e.Data) : e.Data, e.CreatedAt)).ToArray();

    private static string ApprovalText(string data)
    {
        try
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            if (root.TryGetProperty("Decision", out var decision) && decision.ValueKind != JsonValueKind.Null)
                return $"Decision: {decision}";
            if (root.TryGetProperty("Allowed", out var allowed)) return allowed.GetBoolean() ? "Allowed" : "Declined";
            if (root.TryGetProperty("Summary", out var summary)) return summary.GetString() ?? "Review on Desktop";
            return "Review on Desktop";
        }
        catch (JsonException) { return "Review on Desktop"; }
    }
}

/// <summary>Small durable offline copy; Desktop remains the conversation source.</summary>
public sealed class WristInbox
{
    private readonly string _path;
    public WristInbox(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Concierge", "wrist-inbox.json");
        try { if (File.Exists(_path)) Snapshot = JsonSerializer.Deserialize<WristSnapshot>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public WristSnapshot Snapshot { get; private set; } = new();
    public bool IsUnread(WristConversation conversation)
        => conversation.LastMessageSeq >= 0 && (!Snapshot.ReadThrough.TryGetValue(conversation.Id, out var seq) || seq < conversation.LastMessageSeq);
    public void MarkRead(Guid id, int seq)
    {
        var read = new Dictionary<Guid, int>(Snapshot.ReadThrough);
        read[id] = Math.Max(seq, read.GetValueOrDefault(id, -1));
        Snapshot = Snapshot with { ReadThrough = read };
        Save();
    }
    public void Save(IReadOnlyList<WristConversation>? conversations = null, Guid? id = null, IReadOnlyList<WristMessage>? messages = null)
    {
        var threads = new Dictionary<Guid, IReadOnlyList<WristMessage>>(Snapshot.Threads);
        if (id is { } thread && messages is not null) threads[thread] = messages.TakeLast(100).ToArray();
        var recent = (conversations ?? Snapshot.Conversations).Take(50).ToArray();
        var keep = recent.Select(c => c.Id).ToHashSet();
        foreach (var old in threads.Keys.Where(k => !keep.Contains(k)).ToArray()) threads.Remove(old);
        Snapshot = new(recent, threads, DateTimeOffset.UtcNow) { ReadThrough = Snapshot.ReadThrough.Where(p => keep.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value) };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Snapshot));
        File.Move(temporary, _path, true);
    }
}

public sealed record WristSnapshot
{
    public IReadOnlyList<WristConversation> Conversations { get; init; } = [];
    public Dictionary<Guid, IReadOnlyList<WristMessage>> Threads { get; init; } = new();
    public Dictionary<Guid, int> ReadThrough { get; init; } = new();
    public DateTimeOffset? ObservedAt { get; init; }
    public WristSnapshot() { }
    public WristSnapshot(IReadOnlyList<WristConversation> conversations, Dictionary<Guid, IReadOnlyList<WristMessage>> threads, DateTimeOffset observedAt)
        => (Conversations, Threads, ObservedAt) = (conversations, threads, observedAt);
}
