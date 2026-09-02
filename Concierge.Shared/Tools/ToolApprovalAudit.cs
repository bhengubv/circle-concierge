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
    ToolApprovalDecision Decision);

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

    public AuditingToolApprovalService(IToolApprovalService inner, IToolApprovalAuditLog audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public async ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var decision = await _inner.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        _audit.Record(new ToolApprovalAuditEntry(
            DateTimeOffset.UtcNow,
            request.ToolName,
            request.Summary,
            decision));
        return decision;
    }
}
