using Concierge.Shared.Chat;

namespace Concierge.Shared.Context;

/// <summary>
/// How much of the model's window a conversation currently occupies.
/// </summary>
/// <param name="Tokens">Estimated tokens the conversation would cost to send.</param>
/// <param name="BudgetTokens">The window the estimate is measured against.</param>
/// <param name="Fraction">
/// <see cref="Tokens"/> over <see cref="BudgetTokens"/>. Greater than one means the
/// conversation no longer fits.
/// </param>
/// <param name="IsUnderPressure">
/// Whether the caller should reduce the conversation before the next request.
/// </param>
public sealed record ContextUsage(
    int Tokens,
    int BudgetTokens,
    double Fraction,
    bool IsUnderPressure);

/// <summary>
/// Estimates what a conversation costs in tokens so callers can act before a request is
/// refused for length.
/// </summary>
/// <remarks>
/// An estimate, not a tokenizer. It must be cheap enough to run before every request, stable
/// for the same input, and never optimistic — under-counting is what overflows a window,
/// while over-counting only compacts slightly early.
/// </remarks>
public interface ITokenMeter
{
    /// <summary>Estimated tokens for one piece of text.</summary>
    int EstimateTokens(string? text);

    /// <summary>Measure a whole conversation against a window.</summary>
    ContextUsage Measure(IReadOnlyList<ChatTurn> turns, int budgetTokens);
}

/// <summary>
/// A character-class heuristic that runs in a single pass and never needs the model loaded.
/// </summary>
/// <remarks>
/// <para>
/// Latin text averages close to four characters per token, so ASCII is counted at that rate.
/// CJK, and anything else outside the Basic Latin range, commonly costs about one token per
/// character and sometimes more, so non-ASCII is counted at one — the pessimistic side of
/// real tokenizer behaviour, which is the side that keeps the window safe.
/// </para>
/// <para>
/// The pressure threshold is 80% of the window. Compaction needs room to run — it makes a
/// model call of its own — so waiting for 100% means discovering the problem too late.
/// </para>
/// </remarks>
public sealed class HeuristicTokenMeter : ITokenMeter
{
    /// <summary>Characters of plain ASCII text that typically make one token.</summary>
    private const double AsciiCharactersPerToken = 4.0;

    /// <summary>Bytes of structural overhead per turn: role name, separators, framing.</summary>
    private const int PerTurnOverheadTokens = 4;

    /// <summary>Fraction of the window at which a caller should reduce the conversation.</summary>
    private const double PressureThreshold = 0.8;

    /// <inheritdoc />
    public int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var ascii = 0;
        var wide = 0;
        foreach (var character in text)
        {
            if (character < 0x80)
            {
                ascii++;
            }
            else
            {
                wide++;
            }
        }

        var estimate = (int)Math.Ceiling(ascii / AsciiCharactersPerToken) + wide;

        // Any non-empty text costs something; rounding must never reach zero.
        return Math.Max(1, estimate);
    }

    /// <inheritdoc />
    public ContextUsage Measure(IReadOnlyList<ChatTurn> turns, int budgetTokens)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var tokens = 0;
        foreach (var turn in turns)
        {
            tokens += EstimateTokens(turn.Content) + PerTurnOverheadTokens;
        }

        if (budgetTokens <= 0)
        {
            // No window to fit into. Report full rather than dividing by zero: the value is
            // rendered in progress bars and multiplied downstream, so it must stay finite.
            // Always pressure, so no caller proceeds on a meaningless measurement.
            return new ContextUsage(tokens, budgetTokens, tokens == 0 ? 0.0 : 1.0, IsUnderPressure: true);
        }

        var fraction = (double)tokens / budgetTokens;
        return new ContextUsage(tokens, budgetTokens, fraction, fraction >= PressureThreshold);
    }
}
