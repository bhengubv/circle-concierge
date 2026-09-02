using System.Text;
using Concierge.Shared.Chat;

namespace Concierge.Shared.Context;

/// <summary>The outcome of considering a conversation for compaction.</summary>
/// <param name="Turns">The conversation to use from now on — the original when nothing changed.</param>
/// <param name="WasCompacted">Whether anything was replaced.</param>
/// <param name="TokensReclaimed">Estimated tokens freed.</param>
public sealed record CompactionResult(IReadOnlyList<ChatTurn> Turns, bool WasCompacted, int TokensReclaimed);

/// <summary>
/// Decides when a conversation is too long and replaces its older part with a summary.
/// </summary>
public interface ICompactionEngine
{
    /// <summary>Compact only if the conversation is under pressure.</summary>
    Task<CompactionResult> CompactIfNeededAsync(
        IReadOnlyList<ChatTurn> turns,
        int budgetTokens,
        CancellationToken cancellationToken = default);

    /// <summary>Compact now, whether or not the conversation is under pressure.</summary>
    Task<CompactionResult> CompactNowAsync(
        IReadOnlyList<ChatTurn> turns,
        int budgetTokens,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Summarises the older half of a conversation with the model itself, keeping the system
/// prompt and the most recent exchanges verbatim.
/// </summary>
/// <remarks>
/// <para>
/// Three things are never summarised: the system prompt, because it defines who the
/// assistant is; the most recent turns, because the next reply depends on their exact
/// wording; and anything at all if the summary comes back empty or the model fails — an
/// over-full conversation is recoverable, a conversation with a hole in it is not.
/// </para>
/// <para>
/// The summary is inserted as a system turn so a model that distinguishes roles reads it as
/// context rather than as something a person said.
/// </para>
/// </remarks>
public sealed class BasicCompactionEngine : ICompactionEngine
{
    /// <summary>Turns at the end kept word for word.</summary>
    private const int RetainedRecentTurns = 6;

    /// <summary>Below this, there is nothing worth summarising.</summary>
    private const int MinimumTurnsToCompact = 4;

    private const string SummaryPrefix = "Summary of earlier conversation: ";

    private readonly ITokenMeter _meter;
    private readonly IChatRuntime _summariser;

    public BasicCompactionEngine(ITokenMeter meter, IChatRuntime summariser)
    {
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _summariser = summariser ?? throw new ArgumentNullException(nameof(summariser));
    }

    /// <inheritdoc />
    public async Task<CompactionResult> CompactIfNeededAsync(
        IReadOnlyList<ChatTurn> turns,
        int budgetTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turns);

        if (turns.Count == 0 || !_meter.Measure(turns, budgetTokens).IsUnderPressure)
        {
            return Unchanged(turns);
        }

        return await CompactAsync(turns, budgetTokens, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CompactionResult> CompactNowAsync(
        IReadOnlyList<ChatTurn> turns,
        int budgetTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turns);
        return CompactAsync(turns, budgetTokens, cancellationToken);
    }

    private async Task<CompactionResult> CompactAsync(
        IReadOnlyList<ChatTurn> turns,
        int budgetTokens,
        CancellationToken cancellationToken)
    {
        if (turns.Count < MinimumTurnsToCompact)
        {
            return Unchanged(turns);
        }

        var leadingSystem = turns.Count > 0 && string.Equals(turns[0].Role, "system", StringComparison.OrdinalIgnoreCase)
            ? turns[0]
            : null;

        var firstDroppable = leadingSystem is null ? 0 : 1;
        var lastDroppable = turns.Count - RetainedRecentTurns;
        if (lastDroppable - firstDroppable < MinimumTurnsToCompact / 2)
        {
            return Unchanged(turns);
        }

        var dropped = turns.Skip(firstDroppable).Take(lastDroppable - firstDroppable).ToList();
        var summary = await SummariseAsync(dropped, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            // No usable summary. Keeping an over-full conversation is the safe failure.
            return Unchanged(turns);
        }

        var compacted = new List<ChatTurn>(RetainedRecentTurns + 2);
        if (leadingSystem is not null)
        {
            compacted.Add(leadingSystem);
        }

        compacted.Add(new ChatTurn("system", SummaryPrefix + summary.Trim()));
        compacted.AddRange(turns.Skip(lastDroppable));

        var before = _meter.Measure(turns, budgetTokens).Tokens;
        var after = _meter.Measure(compacted, budgetTokens).Tokens;
        if (after >= before)
        {
            // A summary longer than what it replaced is not a reduction.
            return Unchanged(turns);
        }

        return new CompactionResult(compacted, WasCompacted: true, TokensReclaimed: before - after);
    }

    private async Task<string?> SummariseAsync(IReadOnlyList<ChatTurn> dropped, CancellationToken cancellationToken)
    {
        var transcript = new StringBuilder();
        foreach (var turn in dropped)
        {
            transcript.Append(turn.Role).Append(": ").AppendLine(turn.Content);
        }

        var request = new List<ChatTurn>
        {
            new("system", "Summarise the conversation below in a few sentences. Keep names, numbers, "
                + "decisions and anything the user asked for. Write plainly."),
            new("user", transcript.ToString()),
        };

        try
        {
            var summary = new StringBuilder();
            await foreach (var chunk in _summariser.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                summary.Append(chunk);
            }

            return summary.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Any model failure — unloaded, out of memory, a broken adapter. The caller
            // keeps its conversation; nothing is lost but the opportunity to shrink it.
            return null;
        }
    }

    private static CompactionResult Unchanged(IReadOnlyList<ChatTurn> turns)
        => new(turns, WasCompacted: false, TokensReclaimed: 0);
}
