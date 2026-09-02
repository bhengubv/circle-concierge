namespace Concierge.Shared.Chat;

public interface IConversationStore
{
    Task<IReadOnlyList<Conversation>> ListAsync(string ownerId, CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<Conversation> StartAsync(string ownerId, string? title = null, string? systemPrompt = null, CancellationToken cancellationToken = default);

    Task<ChatMessageRow> AppendAsync(Guid conversationId, string role, string content, string? producedBy = null, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Branch a conversation, copying every message up to and including
    /// <paramref name="throughMessageId"/> into a new one. The original is untouched.
    /// </summary>
    Task<Conversation> ForkAsync(Guid conversationId, Guid throughMessageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conversations belonging to <paramref name="ownerId"/> whose title or content contains
    /// <paramref name="query"/>. A blank query matches nothing.
    /// </summary>
    Task<IReadOnlyList<Conversation>> SearchAsync(string ownerId, string query, CancellationToken cancellationToken = default);

    /// <summary>Render a conversation as portable text the owner can keep.</summary>
    Task<string> ExportAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Append one event to a conversation's log and return it with its assigned sequence.
    /// </summary>
    /// <remarks>
    /// The only way anything enters a conversation. Messages are projected from what is
    /// appended here, so a model-visible input that skips this is invisible on reload.
    /// </remarks>
    Task<ConversationEvent> AppendEventAsync(
        Guid conversationId,
        ConversationEventType type,
        string data,
        string? producedBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>The whole log for a conversation, in order. Empty when it does not exist.</summary>
    Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The history to send a model, derived from the log: the system prompt, then every
    /// event that produces a message, with compaction replacements honoured.
    /// </summary>
    Task<IReadOnlyList<ChatTurn>> DeriveMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discard the projected message rows and rebuild them from the log.
    /// </summary>
    /// <remarks>
    /// Exists to prove the log is the source. If this cannot reproduce the conversation,
    /// something reached the model without being recorded.
    /// </remarks>
    Task RebuildProjectionAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
