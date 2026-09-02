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
    // Lazy schema bootstrap. The hosted-service initializer (ConciergeChatSchemaInitializer)
    // is the primary path, but on MAUI BlazorWebView the hosted-service lifecycle is fragile
    // — a single dropped StartAsync leaves the SQLite file empty and every call here throws
    // "no such table: Conversations". The Lazy<Task> wraps EnsureCreated so the first public
    // method awaits it once and every subsequent call sees a completed Task. EnsureCreated
    // itself is idempotent: if the schema already exists it returns immediately.
    private readonly Lazy<Task> _schemaReady;

    public ConversationStore(IDbContextFactory<ConciergeChatDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _schemaReady = new Lazy<Task>(EnsureSchemaAsync);
    }

    private async Task EnsureSchemaAsync()
    {
        await using var db = await _factory.CreateDbContextAsync().ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
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
        await _schemaReady.Value.ConfigureAwait(false);
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
        await _schemaReady.Value.ConfigureAwait(false);
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
        await _schemaReady.Value.ConfigureAwait(false);
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

    // The append lock. SQLite serialises writers, but the read-then-write that assigns a
    // sequence number is two statements: without this, two concurrent appends can read the
    // same maximum and collide on the unique index. The MAUI host runs UI and background
    // work in one process, so this is a real race, not a theoretical one.
    private static readonly SemaphoreSlim AppendGate = new(1, 1);

    /// <inheritdoc />
    public async Task<ConversationEvent> AppendEventAsync(
        Guid conversationId,
        ConversationEventType type,
        string data,
        string? producedBy = null,
        CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
        await AppendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");

            var next = await db.Events
                .Where(e => e.ConversationId == conversationId)
                .Select(e => (int?)e.Seq)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false);

            var entry = new ConversationEvent
            {
                ConversationId = conversationId,
                Seq = (next ?? -1) + 1,
                Type = type,
                Data = data ?? string.Empty,
                ProducedBy = producedBy,
            };
            db.Events.Add(entry);

            // The message rows are a projection maintained alongside the append, so readers
            // that have not moved to the log keep working unchanged.
            if (ProjectsToMessage(type))
            {
                db.Messages.Add(new ChatMessageRow
                {
                    ConversationId = conversationId,
                    Role = RoleFor(type),
                    Content = entry.Data,
                    ProducedBy = producedBy,
                    CreatedAt = entry.CreatedAt,
                });

                conversation.UpdatedAt = entry.CreatedAt;
                if (conversation.Title == "New conversation" && type == ConversationEventType.UserMessage)
                {
                    var trimmed = entry.Data.ReplaceLineEndings(" ").Trim();
                    conversation.Title = trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            AppendGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationEvent>> ReadEventsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Events
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatTurn>> DeriveMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            return [];
        }

        var events = await ReadEventsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var turns = new List<ChatTurn>();

        if (!string.IsNullOrWhiteSpace(conversation.SystemPrompt))
        {
            turns.Add(new ChatTurn("system", conversation.SystemPrompt));
        }

        // A compaction event shadows everything before the sequence it names, so a replayed
        // log produces the same shortened history the live conversation had.
        var shadowedBefore = events
            .Where(e => e.Type == ConversationEventType.CompactionReplace)
            .Select(e => e.Seq)
            .DefaultIfEmpty(-1)
            .Max();

        foreach (var entry in events)
        {
            if (entry.Seq < shadowedBefore || !ProjectsToMessage(entry.Type))
            {
                continue;
            }

            turns.Add(new ChatTurn(RoleFor(entry.Type), entry.Data));
        }

        return turns;
    }

    /// <inheritdoc />
    public async Task RebuildProjectionAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var stale = await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        db.Messages.RemoveRange(stale);

        var events = await db.Events
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in events.Where(entry => ProjectsToMessage(entry.Type)))
        {
            db.Messages.Add(new ChatMessageRow
            {
                ConversationId = conversationId,
                Role = RoleFor(entry.Type),
                Content = entry.Data,
                ProducedBy = entry.ProducedBy,
                CreatedAt = entry.CreatedAt,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether this kind of event becomes a message the model sees.</summary>
    private static bool ProjectsToMessage(ConversationEventType type) => type switch
    {
        ConversationEventType.UserMessage => true,
        ConversationEventType.AssistantMessage => true,
        ConversationEventType.ToolResult => true,
        _ => false,
    };

    /// <summary>
    /// The role a projected event carries. A tool result is its own role — writing it as a
    /// user message is exactly the confusion the log exists to remove.
    /// </summary>
    private static string RoleFor(ConversationEventType type) => type switch
    {
        ConversationEventType.UserMessage => "user",
        ConversationEventType.AssistantMessage => "assistant",
        ConversationEventType.ToolResult => "tool",
        _ => "system",
    };

    /// <inheritdoc />
    public async Task<Conversation> ForkAsync(Guid conversationId, Guid throughMessageId, CancellationToken cancellationToken = default)
    {
        var source = await GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");

        var cut = source.Messages.FindIndex(message => message.Id == throughMessageId);
        if (cut < 0)
        {
            throw new InvalidOperationException($"Message {throughMessageId} is not in conversation {conversationId}.");
        }

        // The system prompt travels with the fork: it defines who the assistant is, and a
        // branch that loses it is not the same conversation continued.
        var fork = await StartAsync(source.OwnerId, $"{source.Title} (branch)", source.SystemPrompt, cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in source.Messages.Take(cut + 1))
        {
            await AppendAsync(fork.Id, message.Role, message.Content, message.ProducedBy, cancellationToken)
                .ConfigureAwait(false);
        }

        return (await GetAsync(fork.Id, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Conversation>> SearchAsync(string ownerId, string query, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);

        // A blank query matching everything would make "search" a synonym for "list", which
        // is how a user accidentally exports their whole history.
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var term = query.Trim();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var matchedByMessage = db.Messages
            .Where(message => EF.Functions.Like(message.Content, $"%{term}%"))
            .Select(message => message.ConversationId);

        return await db.Conversations
            .AsNoTracking()
            .Where(conversation => conversation.OwnerId == ownerId
                && (EF.Functions.Like(conversation.Title, $"%{term}%") || matchedByMessage.Contains(conversation.Id)))
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var conversation = await GetAsync(conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");

        var builder = new System.Text.StringBuilder();
        builder.Append("# ").AppendLine(conversation.Title);
        builder.Append("Started ").AppendLine(conversation.CreatedAt.ToString("u"));
        if (!string.IsNullOrWhiteSpace(conversation.SystemPrompt))
        {
            builder.AppendLine().AppendLine("## System").AppendLine(conversation.SystemPrompt);
        }

        foreach (var message in conversation.Messages)
        {
            builder.AppendLine();
            builder.Append("## ").Append(message.Role);
            if (!string.IsNullOrWhiteSpace(message.ProducedBy))
            {
                builder.Append(" (").Append(message.ProducedBy).Append(')');
            }

            builder.AppendLine().AppendLine(message.Content);
        }

        return builder.ToString();
    }

    public async Task DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
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
