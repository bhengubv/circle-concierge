using System.ComponentModel.DataAnnotations;

namespace Concierge.Shared.Chat;

/// <summary>
/// A single chat thread. One conversation = one ordered list of messages plus a system
/// prompt and per-thread metadata. Multi-user support is deferred — for v1 every row
/// has a placeholder owner ("local") so the column doesn't need to be backfilled later.
/// </summary>
public sealed class Conversation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string OwnerId { get; set; } = "local";

    [MaxLength(200)]
    public string Title { get; set; } = "New conversation";

    [MaxLength(8_000)]
    public string SystemPrompt { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ChatMessageRow> Messages { get; set; } = new();
}

public sealed class ChatMessageRow
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    public Conversation? Conversation { get; set; }

    /// <summary>"system" / "user" / "assistant" — matches CircleAI.Inference.ChatMessage.Role.</summary>
    [MaxLength(16)]
    public string Role { get; set; } = "user";

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Engine name + model tag that produced this message (assistant rows only). Lets the UI
    /// label a turn as e.g. "Qwen3-30B-A3B-Q4" without re-resolving the runtime snapshot.
    /// </summary>
    [MaxLength(128)]
    public string? ProducedBy { get; set; }
}
