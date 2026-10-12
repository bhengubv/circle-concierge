using Concierge.Shared.Chat;

namespace Concierge.Away;

public static class WristReadModel
{
    public static async Task<IReadOnlyList<WristConversation>> ReadAsync(IConversationStore store, BackgroundRuns? runs, CancellationToken ct)
    {
        var recent = (await store.ListAsync("local", ct).ConfigureAwait(false)).OrderByDescending(c => c.UpdatedAt).Take(50);
        var result = new List<WristConversation>();
        foreach (var thread in recent)
        {
            var events = await store.ReadRecentEventsAsync(thread.Id, 100, ct).ConfigureAwait(false);
            result.Add(new(thread.Id, thread.Title, thread.UpdatedAt,
                WristTimeline.Status(events, runs?.IsRunning(thread.Id) == true),
                events.LastOrDefault(e => e.Type is ConversationEventType.AssistantMessage or ConversationEventType.UserMessage)?.Seq ?? -1));
        }
        return result;
    }
}
