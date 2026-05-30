using Microsoft.EntityFrameworkCore;

namespace Concierge.Shared.Chat;

/// <summary>
/// EF Core implementation of <see cref="IConversationStore"/>. Each public call opens its
/// own DbContext scope via the injected factory so the store can be used safely from
/// long-lived Blazor circuits and the MAUI single-process host alike.
/// </summary>
public sealed class ConversationStore : IConversationStore
{
    private readonly IDbContextFactory<ConciergeChatDbContext> _factory;

    public ConversationStore(IDbContextFactory<ConciergeChatDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Conversations
            .AsNoTracking()
            .Where(c => c.OwnerId == ownerId)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Conversation?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
        {
            return null;
        }

        // Messages are pulled as a second query so the projection-side OrderBy stays simple
        // (SQLite cannot translate an OrderBy clause that sits inside an Include lambda even
        // with the Unix-ms value converter applied to the timestamp column).
        conversation.Messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return conversation;
    }

    public async Task<Conversation> StartAsync(string ownerId, string? title = null, string? systemPrompt = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = new Conversation
        {
            OwnerId = ownerId,
            Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim(),
            SystemPrompt = systemPrompt ?? string.Empty,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    public async Task<ChatMessageRow> AppendAsync(Guid conversationId, string role, string content, string? producedBy = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");

        var message = new ChatMessageRow
        {
            ConversationId = conversationId,
            Role = role,
            Content = content,
            ProducedBy = producedBy,
        };
        db.Messages.Add(message);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;

        // Auto-name the conversation from the first user turn so the sidebar is readable.
        if (conversation.Title == "New conversation" && role == "user")
        {
            var trimmed = content.Replace('\n', ' ').Trim();
            conversation.Title = trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return message;
    }

    public async Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
        {
            return;
        }
        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
