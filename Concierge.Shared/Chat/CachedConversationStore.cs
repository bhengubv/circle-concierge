using Concierge.Shared.Caching;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Chat;

/// <summary>Short-lived presentation snapshots. Durable events and model history stay on the underlying store.</summary>
public sealed class CachedConversationStore(IConversationStore inner, TimeProvider? clock = null) : IConversationStore
{
    public Task<IReadOnlyList<ConversationEvent>> ReadArtifactEventsAsync(string ownerId, CancellationToken cancellationToken = default)
        => inner.ReadArtifactEventsAsync(ownerId, cancellationToken);
    private readonly AsyncSnapshotCache<Guid, Conversation?> _threads = new(16, 524_288,
        c => c is null ? 0 : Size(c), c => c is not null, clock);
    private readonly AsyncSnapshotCache<string, IReadOnlyList<Conversation>> _lists = new(8, 65_536,
        list => list.Sum(Size), clock: clock);
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(1);
    public SnapshotCacheMetrics ThreadMetrics => _threads.Metrics;
    public SnapshotCacheMetrics ListMetrics => _lists.Metrics;

    public async Task<Conversation?> GetAsync(Guid id, CancellationToken token = default)
    {
        if (ToolApprovalRunContext.Current is not null) return await inner.GetAsync(id, token);
        var value = await _threads.GetAsync(id, Lifetime, async () => Clone(await inner.GetAsync(id, CancellationToken.None)), token);
        return Clone(value);
    }
    public async Task<IReadOnlyList<Conversation>> ListAsync(string ownerId, CancellationToken token = default)
    {
        var value = await _lists.GetAsync(ownerId, Lifetime,
            async () => (await inner.ListAsync(ownerId, CancellationToken.None)).Select(c => Clone(c)!).ToArray(), token);
        return value.Select(c => Clone(c)!).ToArray();
    }
    private void Clear() { _threads.Invalidate(); _lists.Invalidate(); }
    private async Task<T> Write<T>(Func<Task<T>> action)
    {
        Clear();
        try { return await action().ConfigureAwait(false); }
        finally { Clear(); }
    }
    private async Task Write(Func<Task> action)
    {
        Clear();
        try { await action().ConfigureAwait(false); }
        finally { Clear(); }
    }
    public Task<Conversation> StartAsync(string ownerId, string? title = null, string? systemPrompt = null, CancellationToken cancellationToken = default)
        => Write(() => inner.StartAsync(ownerId, title, systemPrompt, cancellationToken));
    public Task<Conversation> StartWithIdAsync(Guid id, string ownerId, string? title = null, string? systemPrompt = null, CancellationToken cancellationToken = default)
        => Write(() => inner.StartWithIdAsync(id, ownerId, title, systemPrompt, cancellationToken));
    public Task<ChatMessageRow> AppendAsync(Guid id, string role, string content, string? producedBy = null, CancellationToken cancellationToken = default)
        => Write(() => inner.AppendAsync(id, role, content, producedBy, cancellationToken));
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Write(() => inner.DeleteAsync(id, cancellationToken));
    public Task<Conversation> ForkAsync(Guid id, Guid throughMessageId, CancellationToken cancellationToken = default)
        => Write(() => inner.ForkAsync(id, throughMessageId, cancellationToken));
    public Task<ConversationEvent> AppendEventAsync(Guid id, ConversationEventType type, string data, string? producedBy = null,
        CancellationToken cancellationToken = default, Guid? eventId = null)
        => Write(() => inner.AppendEventAsync(id, type, data, producedBy, cancellationToken, eventId));
    public Task SynchronizeEventsAsync(Guid id, IReadOnlyList<ConversationEvent> events, CancellationToken cancellationToken = default)
        => Write(() => inner.SynchronizeEventsAsync(id, events, cancellationToken));
    public Task RebuildProjectionAsync(Guid id, CancellationToken cancellationToken = default)
        => Write(() => inner.RebuildProjectionAsync(id, cancellationToken));
    public Task<IReadOnlyList<Conversation>> SearchAsync(string ownerId, string query, CancellationToken cancellationToken = default) => inner.SearchAsync(ownerId, query, cancellationToken);
    public Task<string> ExportAsync(Guid id, CancellationToken cancellationToken = default) => inner.ExportAsync(id, cancellationToken);
    public Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(Guid id, CancellationToken cancellationToken = default) => inner.ReadEventsAsync(id, cancellationToken);
    public Task<IReadOnlyList<ConversationEvent>> ReadEventsAfterAsync(Guid id, int afterSeq, CancellationToken cancellationToken = default) => inner.ReadEventsAfterAsync(id, afterSeq, cancellationToken);
    public Task<IReadOnlyList<ConversationEvent>> ReadEventsPageAsync(Guid id, int afterSeq, int pageSize, CancellationToken cancellationToken = default) => inner.ReadEventsPageAsync(id, afterSeq, pageSize, cancellationToken);
    public Task<IReadOnlyList<ConversationEvent>> ReadRecentEventsAsync(Guid id, int count = 100, CancellationToken cancellationToken = default) => inner.ReadRecentEventsAsync(id, count, cancellationToken);
    public Task<int?> GetLastEventSequenceAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetLastEventSequenceAsync(id, cancellationToken);
    public Task<IReadOnlyList<ChatTurn>> DeriveMessagesAsync(Guid id, CancellationToken cancellationToken = default) => inner.DeriveMessagesAsync(id, cancellationToken);

    private static long Size(Conversation c) => 128L + c.OwnerId.Length + c.Title.Length + c.SystemPrompt.Length
        + c.Messages.Sum(m => 96L + m.Role.Length + m.Content.Length + (m.ProducedBy?.Length ?? 0));
    private static Conversation? Clone(Conversation? c)
    {
        if (c is null) return null;
        var result = new Conversation { Id = c.Id, OwnerId = c.OwnerId, Title = c.Title,
            SystemPrompt = c.SystemPrompt, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt };
        result.Messages = c.Messages.Select(m => new ChatMessageRow { Id = m.Id, ConversationId = m.ConversationId,
            Conversation = m.Conversation is null ? null : result, Role = m.Role, Content = m.Content,
            ProducedBy = m.ProducedBy, CreatedAt = m.CreatedAt }).ToList();
        return result;
    }
}
