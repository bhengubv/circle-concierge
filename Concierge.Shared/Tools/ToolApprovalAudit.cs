using System.Text.Json;

namespace Concierge.Shared.Tools;

/// <summary>One decision, kept so it can be reviewed after the fact.</summary>
/// <param name="At">When the decision was made.</param>
/// <param name="ToolName">The tool that asked.</param>
/// <param name="Summary">The one-line description the person was shown.</param>
/// <param name="Decision">What they answered — including <see cref="ToolApprovalDecision.Unavailable"/>
/// when nobody could be asked, since a call refused for lack of an approver is as worth
/// knowing about as one a person turned down.</param>
public sealed record ToolApprovalAuditEntry(
    DateTimeOffset At,
    string ToolName,
    string Summary,
    ToolApprovalDecision Decision,
    Guid? ConversationId = null,
    Guid? RunId = null,
    Guid? ApprovalId = null);

/// <summary>One approval event stored alongside the conversation that owns the run.</summary>
public sealed record ConversationApprovalEvent(
    Guid ApprovalId,
    Guid RunId,
    string ToolName,
    string Summary,
    ConciergeToolRisk Risk,
    string? Detail,
    DateTimeOffset At,
    ToolApprovalDecision? Decision = null);

/// <summary>
/// Where approval decisions go so a parent, an operator, or a later session can see what was
/// asked and what was answered. Mirrors the role <c>ISafetyAuditLog</c> plays for content.
/// </summary>
public interface IToolApprovalAuditLog
{
    /// <summary>Record one settled decision.</summary>
    void Record(ToolApprovalAuditEntry entry);

    /// <summary>Every decision recorded so far, oldest first.</summary>
    IReadOnlyList<ToolApprovalAuditEntry> Entries { get; }
}

/// <summary>
/// Keeps decisions in memory, and appends each one to a JSON-lines file when a path is
/// supplied so the record survives the process. A failed append never breaks the call that
/// produced it — losing an audit line must not also lose the user's work.
/// </summary>
public sealed class ToolApprovalAuditLog : IToolApprovalAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly List<ToolApprovalAuditEntry> _entries = [];
    private readonly string? _path;

    /// <summary>An in-memory log — used by tests and by hosts with nowhere to write.</summary>
    public ToolApprovalAuditLog() : this(null)
    {
    }

    /// <param name="path">JSON-lines file to append to, or null to stay in memory.</param>
    public ToolApprovalAuditLog(string? path) => _path = path;

    /// <inheritdoc />
    public IReadOnlyList<ToolApprovalAuditEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    /// <inheritdoc />
    public void Record(ToolApprovalAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
            if (_path is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            }
            catch (IOException)
            {
                // The in-memory copy stands. A full or locked disk must not fail the tool call.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning: the audit is evidence, not a precondition.
            }
        }
    }
}

/// <summary>
/// Wraps any <see cref="IToolApprovalService"/> and records what was asked and what was
/// answered. Auditing lives here rather than in each tool so a tool cannot forget to do it.
/// </summary>
public sealed class AuditingToolApprovalService : IToolApprovalService
{
    private readonly IToolApprovalService _inner;
    private readonly IToolApprovalAuditLog _audit;
    private readonly Concierge.Shared.Chat.IConversationStore? _conversations;

    public AuditingToolApprovalService(
        IToolApprovalService inner,
        IToolApprovalAuditLog audit,
        Concierge.Shared.Chat.IConversationStore? conversations = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _conversations = conversations;
    }

    /// <inheritdoc />
    public async ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = ToolApprovalRunContext.Current;
        var promptsPerson = _inner is InteractiveToolApprovalService interactive && interactive.WillPromptPerson;
        var approvalId = promptsPerson ? Guid.NewGuid() : (Guid?)null;
        var effectiveRequest = approvalId is { } id ? request with { ApprovalId = id } : request;

        if (promptsPerson && context is not null && _conversations is not null && approvalId is { } askedId)
        {
            var asked = new ConversationApprovalEvent(
                askedId, context.RunId, request.ToolName, request.Summary, request.Risk,
                request.Detail, DateTimeOffset.UtcNow);
            await _conversations.AppendEventAsync(
                context.ConversationId,
                Concierge.Shared.Chat.ConversationEventType.ApprovalAsked,
                JsonSerializer.Serialize(asked),
                cancellationToken: cancellationToken,
                eventId: askedId).ConfigureAwait(false);
        }

        var decision = await _inner.RequestAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);

        if (promptsPerson && context is not null && _conversations is not null && approvalId is { } decidedId)
        {
            var decided = new ConversationApprovalEvent(
                decidedId, context.RunId, request.ToolName, request.Summary, request.Risk,
                request.Detail, DateTimeOffset.UtcNow, decision);
            await _conversations.AppendEventAsync(
                context.ConversationId,
                Concierge.Shared.Chat.ConversationEventType.ApprovalDecided,
                JsonSerializer.Serialize(decided),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        _audit.Record(new ToolApprovalAuditEntry(
            DateTimeOffset.UtcNow,
            request.ToolName,
            request.Summary,
            decision,
            context?.ConversationId,
            context?.RunId,
            approvalId));
        return decision;
    }
}
