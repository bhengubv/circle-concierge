using System.Text;

namespace Concierge.Shared.Context;

/// <summary>The outcome of pruning one tool result.</summary>
/// <param name="Text">What the model should see.</param>
/// <param name="WasPruned">Whether anything was removed.</param>
/// <param name="RemovedChars">How many characters were dropped.</param>
public sealed record PrunedToolResult(string Text, bool WasPruned, int RemovedChars);

/// <summary>
/// Reduces an oversized tool result before it becomes part of the conversation.
/// </summary>
/// <remarks>
/// Applied at the moment a result is produced, not when the window later fills. A result
/// that enters the conversation whole is paid for on every subsequent request, so trimming
/// it once at the source is far cheaper than compacting it repeatedly afterwards.
/// </remarks>
public interface IToolResultPruner
{
    /// <summary>Prune one tool result, returning it unchanged when it is small enough.</summary>
    PrunedToolResult Prune(string? output);
}

/// <summary>
/// Keeps the head and the tail of an oversized result and replaces the middle with a line
/// saying what was removed.
/// </summary>
/// <remarks>
/// Head and tail are both kept because they carry different information: a command announces
/// what it is doing at the start and reports how it went at the end. Keeping only the head
/// discards the error message that made the output worth reading.
/// </remarks>
public sealed class HeadTailToolResultPruner : IToolResultPruner
{
    private readonly int _thresholdChars;
    private readonly int _headChars;
    private readonly int _tailChars;

    /// <param name="thresholdChars">Results at or below this length are untouched.</param>
    /// <param name="headChars">Characters kept from the start.</param>
    /// <param name="tailChars">Characters kept from the end.</param>
    public HeadTailToolResultPruner(int thresholdChars = 8_192, int headChars = 4_096, int tailChars = 1_024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thresholdChars);
        ArgumentOutOfRangeException.ThrowIfNegative(headChars);
        ArgumentOutOfRangeException.ThrowIfNegative(tailChars);

        _thresholdChars = thresholdChars;
        _headChars = headChars;
        _tailChars = tailChars;
    }

    /// <inheritdoc />
    public PrunedToolResult Prune(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= _thresholdChars)
        {
            return new PrunedToolResult(output ?? string.Empty, WasPruned: false, RemovedChars: 0);
        }

        var head = Math.Min(_headChars, output.Length);
        var tail = Math.Min(_tailChars, output.Length - head);
        var removed = output.Length - head - tail;
        if (removed <= 0)
        {
            return new PrunedToolResult(output, WasPruned: false, RemovedChars: 0);
        }

        var builder = new StringBuilder(head + tail + 64);
        builder.Append(output, 0, head);
        builder.Append("\n… ").Append(removed).Append(" characters removed …\n");
        builder.Append(output, output.Length - tail, tail);

        var text = builder.ToString();

        // The marker is inserted text, so a result only just over the threshold could come
        // back longer than it went in. Returning the original is both smaller and truthful.
        return text.Length >= output.Length
            ? new PrunedToolResult(output, WasPruned: false, RemovedChars: 0)
            : new PrunedToolResult(text, WasPruned: true, RemovedChars: removed);
    }
}
