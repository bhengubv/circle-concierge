using System.ComponentModel.DataAnnotations;

namespace Concierge.Shared.Chat;

/// <summary>
/// What kind of thing happened. The set is closed on purpose: a new kind of model-visible
/// input means a new member here, which is what keeps "what the model saw" and "what is in
/// the log" the same thing.
/// </summary>
public enum ConversationEventType
{
    /// <summary>A turn opened. Bookkeeping — never shown to the model.</summary>
    TurnStart = 0,

    /// <summary>A turn closed. Bookkeeping — never shown to the model.</summary>
    TurnEnd = 1,

    /// <summary>Something a person said.</summary>
    UserMessage = 2,

    /// <summary>Something the model said.</summary>
    AssistantMessage = 3,

    /// <summary>The model asked for a tool.</summary>
    ToolCall = 4,

    /// <summary>What the tool returned. Distinct from a user message, which is the whole point.</summary>
    ToolResult = 5,

    /// <summary>A person was asked to approve something. Log-only.</summary>
    ApprovalAsked = 6,

    /// <summary>What they answered. Log-only.</summary>
    ApprovalDecided = 7,

    /// <summary>An older span was replaced by a summary. Log-only; changes what derives.</summary>
    CompactionReplace = 8,

    /// <summary>The model rewrote its task list. Log-only.</summary>
    TodoWrite = 9,
}

/// <summary>
/// One immutable entry in a conversation's log.
/// </summary>
/// <remarks>
/// Rows are never updated or deleted. <see cref="Seq"/> is contiguous from zero within a
/// conversation, so a reader can tell whether it has the whole story — which is what makes
/// the log safe to carry across the mesh in pieces.
/// </remarks>
public sealed class ConversationEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    /// <summary>Position in this conversation's log, contiguous from zero.</summary>
    public int Seq { get; set; }

    public ConversationEventType Type { get; set; }

    /// <summary>
    /// The event's payload. Plain text for messages, JSON for structured events. Opaque to
    /// the log itself — only the projection interprets it.
    /// </summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>
    /// Which engine produced this, for assistant events. Kept so a past turn can still be
    /// labelled after the runtime has swapped underneath it.
    /// </summary>
    [MaxLength(128)]
    public string? ProducedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
