using Concierge.Shared.Planning;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What repeat detection must do (parity feature 6): notice when the model is going round in
/// circles and say so, instead of letting it spend the whole iteration budget.
/// </summary>
/// <remarks>
/// A small model stuck in a loop will call the same tool with the same arguments until the
/// cap stops it. The user sees five wasted turns and no answer. Telling the model plainly
/// that it has already done this is cheaper than any of those turns.
/// </remarks>
public sealed class RepeatToolReminderTests
{
    private readonly IRepeatToolReminder _reminder = new RepeatToolReminder(thresholds: [3, 5]);

    [Fact]
    public void A_first_call_draws_no_comment()
    {
        Assert.Null(_reminder.Observe("read_file", """{"path":"a.txt"}"""));
    }

    [Fact]
    public void Two_identical_calls_draw_no_comment()
    {
        _reminder.Observe("read_file", """{"path":"a.txt"}""");

        Assert.Null(_reminder.Observe("read_file", """{"path":"a.txt"}"""));
    }

    [Fact]
    public void A_third_identical_call_is_pointed_out()
    {
        _reminder.Observe("read_file", """{"path":"a.txt"}""");
        _reminder.Observe("read_file", """{"path":"a.txt"}""");

        Assert.NotNull(_reminder.Observe("read_file", """{"path":"a.txt"}"""));
    }

    [Fact]
    public void The_reminder_names_the_tool_and_the_count()
    {
        _reminder.Observe("read_file", "{}");
        _reminder.Observe("read_file", "{}");

        var reminder = _reminder.Observe("read_file", "{}");

        Assert.Contains("read_file", reminder!, StringComparison.Ordinal);
        Assert.Contains("3", reminder, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reminder_is_not_repeated_on_every_later_call()
    {
        for (var i = 0; i < 3; i++)
        {
            _reminder.Observe("read_file", "{}");
        }

        Assert.Null(_reminder.Observe("read_file", "{}"));
    }

    [Fact]
    public void A_later_threshold_speaks_again()
    {
        for (var i = 0; i < 4; i++)
        {
            _reminder.Observe("read_file", "{}");
        }

        Assert.NotNull(_reminder.Observe("read_file", "{}"));
    }

    [Fact]
    public void Different_arguments_are_not_a_repeat()
    {
        _reminder.Observe("read_file", """{"path":"a.txt"}""");
        _reminder.Observe("read_file", """{"path":"b.txt"}""");

        Assert.Null(_reminder.Observe("read_file", """{"path":"c.txt"}"""));
    }

    [Fact]
    public void A_different_tool_resets_the_count()
    {
        _reminder.Observe("read_file", "{}");
        _reminder.Observe("read_file", "{}");
        _reminder.Observe("grep", "{}");

        Assert.Null(_reminder.Observe("read_file", "{}"));
    }
}

/// <summary>
/// What plan mode must do (parity feature 24): be a state that actually withholds the tools,
/// not an instruction asking the model to behave.
/// </summary>
/// <remarks>
/// Harness makes the distinction explicit — a rule the model can ignore is not a mode. Here
/// plan mode composes the session read-only, so the question of whether the model obeys
/// never arises.
/// </remarks>
public sealed class PlanModeTests
{
    [Fact]
    public void A_session_starts_outside_plan_mode()
    {
        Assert.False(new PlanModeState().IsPlanning);
    }

    [Fact]
    public void Entering_plan_mode_is_visible()
    {
        var state = new PlanModeState();

        state.Enter();

        Assert.True(state.IsPlanning);
    }

    [Fact]
    public void Planning_forces_a_read_only_session_whatever_was_asked_for()
    {
        var state = new PlanModeState();
        state.Enter();

        Assert.Equal(
            ConciergePermissionPreset.ReadOnly,
            state.Constrain(ConciergePermissionPolicy.For(ConciergePermissionPreset.FullAccess)).Preset);
    }

    [Fact]
    public void Not_planning_leaves_the_session_as_it_was()
    {
        var state = new PlanModeState();

        Assert.Equal(
            ConciergePermissionPreset.FullAccess,
            state.Constrain(ConciergePermissionPolicy.For(ConciergePermissionPreset.FullAccess)).Preset);
    }

    [Fact]
    public void Leaving_plan_mode_requires_a_plan()
    {
        var state = new PlanModeState();
        state.Enter();

        Assert.Throws<ArgumentException>(() => state.Exit("  "));
    }

    [Fact]
    public void Leaving_plan_mode_keeps_the_plan()
    {
        var state = new PlanModeState();
        state.Enter();

        state.Exit("# Plan\n1. Do the thing");

        Assert.Contains("Do the thing", state.Plan!, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaving_plan_mode_ends_the_planning_state()
    {
        var state = new PlanModeState();
        state.Enter();

        state.Exit("# Plan");

        Assert.False(state.IsPlanning);
    }

    [Fact]
    public void Leaving_a_session_that_was_never_planning_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => new PlanModeState().Exit("# Plan"));
    }

    [Fact]
    public void Re_entering_clears_the_previous_plan()
    {
        var state = new PlanModeState();
        state.Enter();
        state.Exit("# The first plan");

        state.Enter();

        Assert.Null(state.Plan);
    }
}
