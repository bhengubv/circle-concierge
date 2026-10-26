using Microsoft.EntityFrameworkCore;

namespace Concierge.Shared.Chat;

/// <summary>
/// EF Core implementation of <see cref="IConversationStore"/>. Each public call opens its
/// own DbContext scope via the injected factory so the store can be used safely from
/// long-lived Blazor circuits and the MAUI single-process host alike.
/// </summary>
public sealed class ConversationStore : IConversationStore
{
    public async Task<IReadOnlyList<ConversationEvent>> ReadArtifactEventsAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await (from entry in db.Events.AsNoTracking()
            join conversation in db.Conversations on entry.ConversationId equals conversation.Id
            where conversation.OwnerId == ownerId && entry.Type == ConversationEventType.ArtifactCreated
            orderby entry.CreatedAt descending
            select entry).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<ConversationEvent>> ReadRecentEventsAsync(Guid conversationId, int count = 100, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var events = await db.Events.AsNoTracking().Where(e => e.ConversationId == conversationId)
            .OrderByDescending(e => e.Seq).Take(Math.Clamp(count, 1, 100)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return events.OrderBy(e => e.Seq).ToArray();
    }

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
            .Where(m => m.ConversationId == conversationId
                && (m.ProducedBy == null || m.ProducedBy != "Connection status"))
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

    public async Task<Conversation> StartWithIdAsync(Guid id, string ownerId, string? title = null, string? systemPrompt = null, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("A conversation ID is required.", nameof(id));
        await _schemaReady.Value.ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = new Conversation
        {
            Id = id,
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
        var type = role.ToLowerInvariant() switch
        {
            "user" => ConversationEventType.UserMessage,
            "assistant" => ConversationEventType.AssistantMessage,
            "tool" => ConversationEventType.ToolResult,
            "system" => ConversationEventType.SystemMessage,
            _ => throw new ArgumentException("Use user, assistant, tool or system role.", nameof(role))
        };
        var entry = await AppendEventAsync(conversationId, type, content, producedBy, cancellationToken).ConfigureAwait(false);
        if (!ProjectsToMessage(type, producedBy))
            return new ChatMessageRow { Id = entry.Id, ConversationId = conversationId, Role = role, Content = content,
                ProducedBy = producedBy, CreatedAt = entry.CreatedAt };
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Messages.AsNoTracking().SingleAsync(m => m.Id == entry.Id, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default,
        Guid? eventId = null)
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

            var previousTime = await db.Events
                .Where(e => e.ConversationId == conversationId)
                .OrderByDescending(e => e.Seq)
                .Select(e => (DateTimeOffset?)e.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var createdAt = DateTimeOffset.UtcNow;
            if (previousTime is { } lastTime && createdAt <= lastTime)
                createdAt = lastTime.AddMilliseconds(1);

            var entry = new ConversationEvent
            {
                Id = eventId is { } suppliedId && suppliedId != Guid.Empty ? suppliedId : Guid.NewGuid(),
                ConversationId = conversationId,
                Seq = (next ?? -1) + 1,
                Type = type,
                Data = data ?? string.Empty,
                ProducedBy = producedBy,
                CreatedAt = createdAt,
            };
            db.Events.Add(entry);

            // The message rows are a projection maintained alongside the append, so readers
            // that have not moved to the log keep working unchanged.
            if (ProjectsToMessage(type, producedBy))
            {
                db.Messages.Add(new ChatMessageRow
                {
                    Id = entry.Id,
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
        => await ReadEventsAfterAsync(conversationId, -1, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationEvent>> ReadEventsAfterAsync(
        Guid conversationId, int afterSeq, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Events
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.Seq > afterSeq)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationEvent>> ReadEventsPageAsync(
        Guid conversationId, int afterSeq, int pageSize, CancellationToken cancellationToken = default)
    {
        var boundedSize = Math.Clamp(pageSize, 1, ConversationEventPage.MaximumSize);
        await _schemaReady.Value.ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Events
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.Seq > afterSeq)
            .OrderBy(e => e.Seq)
            .Take(boundedSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int?> GetLastEventSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Events
            .Where(entry => entry.ConversationId == conversationId)
            .Select(entry => (int?)entry.Seq)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SynchronizeEventsAsync(Guid conversationId, IReadOnlyList<ConversationEvent> events, CancellationToken cancellationToken = default)
    {
        await _schemaReady.Value.ConfigureAwait(false);
        await AppendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");
            var previousTime = await db.Events
                .Where(e => e.ConversationId == conversationId)
                .OrderByDescending(e => e.Seq)
                .Select(e => (DateTimeOffset?)e.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var incoming in events.OrderBy(e => e.Seq))
            {
                if (incoming.ConversationId != conversationId)
                    throw new InvalidOperationException("A synchronized event belongs to a different conversation.");

                if (await db.Events.AnyAsync(e => e.Id == incoming.Id, cancellationToken).ConfigureAwait(false))
                    continue;

                // The originating device records its submitted prompt before sending it.
                // The execution device records the same prompt in its own log. Treat an
                // identical sequence slot as the same event; reject actual divergent logs.
                var sameSlot = await db.Events.FirstOrDefaultAsync(
                    e => e.ConversationId == conversationId && e.Seq == incoming.Seq, cancellationToken).ConfigureAwait(false);
                if (sameSlot is not null)
                {
                    if (sameSlot.Type == incoming.Type && sameSlot.Data == incoming.Data)
                        continue;
                    throw new InvalidOperationException($"Conversation log conflict at sequence {incoming.Seq}.");
                }

                var createdAt = incoming.CreatedAt;
                if (previousTime is { } lastTime && createdAt <= lastTime)
                    createdAt = lastTime.AddMilliseconds(1);
                var copy = new ConversationEvent
                {
                    Id = incoming.Id,
                    ConversationId = conversationId,
                    Seq = incoming.Seq,
                    Type = incoming.Type,
                    Data = incoming.Data,
                    ProducedBy = incoming.ProducedBy,
                    // Event sequence is the cross-device ordering authority. Keep the
                    // projected timeline monotonic when handset and Desktop clocks differ.
                    CreatedAt = createdAt,
                };
                db.Events.Add(copy);
                previousTime = createdAt;
                if (ProjectsToMessage(copy.Type, copy.ProducedBy))
                {
                    db.Messages.Add(new ChatMessageRow
                    {
                        Id = copy.Id,
                        ConversationId = conversationId,
                        Role = RoleFor(copy.Type),
                        Content = copy.Data,
                        ProducedBy = copy.ProducedBy,
                        CreatedAt = copy.CreatedAt,
                    });
                    conversation.UpdatedAt = copy.CreatedAt;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AppendGate.Release();
        }
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
        return ConversationHistoryProjection.Derive(conversation.SystemPrompt, events).Turns;
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

        foreach (var entry in events.Where(entry => ProjectsToMessage(entry.Type, entry.ProducedBy)))
        {
            db.Messages.Add(new ChatMessageRow
            {
                Id = entry.Id,
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
    private static bool ProjectsToMessage(ConversationEventType type, string? producedBy = null)
    {
        // Older builds persisted transient Desktop connection failures as assistant
        // messages. Keep those diagnostics out of the visible transcript and model history.
        if (type == ConversationEventType.AssistantMessage
            && string.Equals(producedBy, "Connection status", StringComparison.Ordinal))
            return false;

        return type switch
    {
        ConversationEventType.UserMessage => true,
        ConversationEventType.AssistantMessage => true,
        ConversationEventType.ToolResult => true,
        ConversationEventType.SystemMessage => true,
        _ => false,
    };
    }

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
