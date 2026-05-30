namespace Concierge.Shared.Chat;

public interface IConversationStore
{
    Task<IReadOnlyList<Conversation>> ListAsync(string ownerId, CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<Conversation> StartAsync(string ownerId, string? title = null, string? systemPrompt = null, CancellationToken cancellationToken = default);

    Task<ChatMessageRow> AppendAsync(Guid conversationId, string role, string content, string? producedBy = null, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
