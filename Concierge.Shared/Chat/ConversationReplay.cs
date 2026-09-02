namespace Concierge.Shared.Chat;

/// <summary>One moment in a replayed conversation.</summary>
/// <param name="Event">What happened at this point.</param>
/// <param name="HistorySoFar">The history the model would have been shown, including this event.</param>
public sealed record ReplayStep(ConversationEvent Event, IReadOnlyList<ChatTurn> HistorySoFar);

/// <summary>
/// Walks a conversation log one event at a time, reconstructing what the model saw at each
/// point.
/// </summary>
/// <remarks>
/// There is no debugger to attach to a phone in a place with no signal. A log you can step
/// through is the only account of what actually happened, and being able to stop at a chosen
/// point is what turns "it went wrong somewhere" into "it went wrong here".
/// </remarks>
public sealed class ConversationReplay
{
    private readonly IReadOnlyList<ConversationEvent> _events;

    public ConversationReplay(IReadOnlyList<ConversationEvent> events)
        => _events = events ?? throw new ArgumentNullException(nameof(events));

    /// <summary>Every step, in order.</summary>
    /// <param name="throughSeq">Stop after this sequence number. Null replays everything.</param>
    public IEnumerable<ReplayStep> Steps(int? throughSeq = null)
    {
        var history = new List<ChatTurn>();

        foreach (var entry in _events.OrderBy(entry => entry.Seq))
        {
            if (throughSeq is { } limit && entry.Seq > limit)
            {
                yield break;
            }

            if (ProjectsToMessage(entry.Type))
            {
                history.Add(new ChatTurn(RoleFor(entry.Type), entry.Data));
            }

            // A copy per step: a caller holding an earlier step must still see the history as
            // it stood then, not as it ends up.
            yield return new ReplayStep(entry, history.ToList());
        }
    }

    private static bool ProjectsToMessage(ConversationEventType type) => type
        is ConversationEventType.UserMessage
        or ConversationEventType.AssistantMessage
        or ConversationEventType.ToolResult;

    private static string RoleFor(ConversationEventType type) => type switch
    {
        ConversationEventType.UserMessage => "user",
        ConversationEventType.AssistantMessage => "assistant",
        ConversationEventType.ToolResult => "tool",
        _ => "system",
    };
}

/// <summary>What checking a log found.</summary>
/// <param name="IsValid">Whether the log is well formed.</param>
/// <param name="Problems">Everything wrong with it, in plain words.</param>
public sealed record LogCheckResult(bool IsValid, IReadOnlyList<string> Problems);

/// <summary>
/// Checks that a conversation log holds together.
/// </summary>
/// <remarks>
/// A log arriving over the mesh comes in pieces and can be incomplete, duplicated, or out of
/// order. Checking it is the difference between knowing that and quietly deriving a
/// conversation that never happened.
/// </remarks>
public static class ConversationLogInvariants
{
    /// <summary>Check a log and report everything wrong with it.</summary>
    public static LogCheckResult Check(IReadOnlyList<ConversationEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var problems = new List<string>();
        if (events.Count == 0)
        {
            return new LogCheckResult(true, problems);
        }

        var ordered = events.OrderBy(entry => entry.Seq).ToList();

        if (ordered[0].Seq != 0)
        {
            problems.Add($"The log starts at {ordered[0].Seq} instead of 0, so its beginning is missing.");
        }

        for (var index = 1; index < ordered.Count; index++)
        {
            var previous = ordered[index - 1].Seq;
            var current = ordered[index].Seq;

            if (current == previous)
            {
                problems.Add($"Sequence {current} appears more than once.");
            }
            else if (current != previous + 1)
            {
                var missing = string.Join(", ", Enumerable.Range(previous + 1, current - previous - 1));
                problems.Add($"Sequence {missing} is missing between {previous} and {current}.");
            }
        }

        CheckToolPairing(ordered, problems);

        return new LogCheckResult(problems.Count == 0, problems);
    }

    /// <summary>
    /// Every tool result must follow a call, and every call must be answered before its turn
    /// ends. An unanswered call leaves the model waiting for something that never arrives; an
    /// unasked result is the bug this whole design exists to make impossible.
    /// </summary>
    private static void CheckToolPairing(List<ConversationEvent> ordered, List<string> problems)
    {
        var outstanding = 0;

        foreach (var entry in ordered)
        {
            switch (entry.Type)
            {
                case ConversationEventType.ToolCall:
                    outstanding++;
                    break;

                case ConversationEventType.ToolResult when outstanding == 0:
                    problems.Add($"The tool result at sequence {entry.Seq} has no call before it.");
                    break;

                case ConversationEventType.ToolResult:
                    outstanding--;
                    break;

                case ConversationEventType.TurnEnd when outstanding > 0:
                    problems.Add($"The turn ending at sequence {entry.Seq} left {outstanding} tool call(s) unanswered.");
                    outstanding = 0;
                    break;
            }
        }

        if (outstanding > 0)
        {
            problems.Add($"The log ends with {outstanding} tool call(s) unanswered.");
        }
    }
}
