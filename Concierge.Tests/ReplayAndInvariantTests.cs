using Concierge.Shared.Chat;

namespace Concierge.Tests;

/// <summary>
/// What replay must do (parity feature 27): walk a stored conversation step by step, so a
/// failure that happened on a device you cannot reach can be understood here.
/// </summary>
/// <remarks>
/// This is the reason the log exists. There is no debugger to attach to a phone in a place
/// with no signal; a log you can step through is the only account of what happened.
/// </remarks>
public sealed class ConversationReplayTests
{
    [Fact]
    public void Replay_visits_every_event_in_order()
    {
        var replay = new ConversationReplay(Log(
            (0, ConversationEventType.TurnStart, "1"),
            (1, ConversationEventType.UserMessage, "a question"),
            (2, ConversationEventType.AssistantMessage, "an answer"),
            (3, ConversationEventType.TurnEnd, "1")));

        Assert.Equal([0, 1, 2, 3], replay.Steps().Select(step => step.Event.Seq));
    }

    [Fact]
    public void Each_step_shows_the_history_as_it_stood_at_that_moment()
    {
        var replay = new ConversationReplay(Log(
            (0, ConversationEventType.UserMessage, "first"),
            (1, ConversationEventType.AssistantMessage, "second"),
            (2, ConversationEventType.UserMessage, "third")));

        var atSecond = replay.Steps().ElementAt(1);

        Assert.Equal(["first", "second"], atSecond.HistorySoFar.Select(turn => turn.Content));
    }

    [Fact]
    public void Bookkeeping_events_do_not_add_to_the_history()
    {
        var replay = new ConversationReplay(Log(
            (0, ConversationEventType.TurnStart, "1"),
            (1, ConversationEventType.UserMessage, "a question")));

        Assert.Empty(replay.Steps().First().HistorySoFar);
    }

    [Fact]
    public void Replay_can_stop_at_a_chosen_point()
    {
        var replay = new ConversationReplay(Log(
            (0, ConversationEventType.UserMessage, "first"),
            (1, ConversationEventType.AssistantMessage, "second"),
            (2, ConversationEventType.UserMessage, "third")));

        Assert.Equal(2, replay.Steps(throughSeq: 1).Count());
    }

    [Fact]
    public void The_history_at_the_end_of_replay_matches_the_whole_conversation()
    {
        var log = Log(
            (0, ConversationEventType.UserMessage, "first"),
            (1, ConversationEventType.AssistantMessage, "second"));
        var replay = new ConversationReplay(log);

        Assert.Equal(2, replay.Steps().Last().HistorySoFar.Count);
    }

    [Fact]
    public void An_empty_log_replays_to_nothing()
    {
        Assert.Empty(new ConversationReplay([]).Steps());
    }

    private static IReadOnlyList<ConversationEvent> Log(
        params (int Seq, ConversationEventType Type, string Data)[] entries)
        => entries
            .Select(entry => new ConversationEvent { Seq = entry.Seq, Type = entry.Type, Data = entry.Data })
            .ToList();
}

/// <summary>
/// What the invariants must do (parity feature 40): fail a malformed log at the point it is
/// read, rather than letting it silently produce a wrong history.
/// </summary>
/// <remarks>
/// A log that arrives over the mesh in pieces can be incomplete or out of order. Checking it
/// is the difference between knowing that and quietly deriving a conversation that never
/// happened.
/// </remarks>
public sealed class ConversationLogInvariantTests
{
    [Fact]
    public void A_well_formed_log_passes()
    {
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.TurnStart, "1"),
            (1, ConversationEventType.UserMessage, "a question"),
            (2, ConversationEventType.ToolCall, """{"name":"read_file"}"""),
            (3, ConversationEventType.ToolResult, "content"),
            (4, ConversationEventType.TurnEnd, "1")));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void An_empty_log_passes()
    {
        Assert.True(ConversationLogInvariants.Check([]).IsValid);
    }

    [Fact]
    public void A_gap_in_the_sequence_fails()
    {
        // The mesh case: a bundle went missing, so this device has part of the story. It must
        // know that rather than derive a conversation with a hole in it.
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.UserMessage, "first"),
            (2, ConversationEventType.UserMessage, "third")));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_gap_says_where_it_is()
    {
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.UserMessage, "first"),
            (2, ConversationEventType.UserMessage, "third")));

        Assert.Contains("1", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_does_not_start_at_zero_fails()
    {
        var result = ConversationLogInvariants.Check(Log(
            (1, ConversationEventType.UserMessage, "where is the beginning")));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_repeated_sequence_number_fails()
    {
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.UserMessage, "first"),
            (0, ConversationEventType.UserMessage, "also first")));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_tool_result_with_no_call_before_it_fails()
    {
        // The bug the log was built to make impossible: a result appearing as though someone
        // had typed it, with nothing that asked for it.
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.UserMessage, "a question"),
            (1, ConversationEventType.ToolResult, "content from nowhere")));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_tool_result_with_a_call_before_it_passes()
    {
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.ToolCall, """{"name":"read_file"}"""),
            (1, ConversationEventType.ToolResult, "content")));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_call_with_no_result_fails()
    {
        // A turn that ended with a call unanswered means the model is still waiting for
        // something that never came.
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.ToolCall, """{"name":"read_file"}"""),
            (1, ConversationEventType.TurnEnd, "1")));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.UserMessage, "first"),
            (2, ConversationEventType.ToolResult, "orphan")));

        Assert.Equal(2, result.Problems.Count);
    }

    [Fact]
    public void A_problem_report_reads_plainly()
    {
        var result = ConversationLogInvariants.Check(Log(
            (0, ConversationEventType.ToolResult, "orphan")));

        Assert.Contains("tool", Assert.Single(result.Problems), StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ConversationEvent> Log(
        params (int Seq, ConversationEventType Type, string Data)[] entries)
        => entries
            .Select(entry => new ConversationEvent { Seq = entry.Seq, Type = entry.Type, Data = entry.Data })
            .ToList();
}
