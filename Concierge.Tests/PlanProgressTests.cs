using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Keeping the stated plan honest about what actually happened.
///
/// Concierge stated a plan and then ticked one step off per round of tool calls
/// regardless of whether the round worked. A run where every call failed still
/// showed "3 of 5" — the strip asserting progress nobody had made, in the place a
/// person looks precisely because they are deciding whether to let it carry on.
/// Same fault as the approvals badge that always said two.
///
/// And nothing ever asked for a rethink. A failed step left the rest of the plan
/// standing on an assumption that was no longer true, and the loop ran to its
/// round cap with no explanation. OpenDroid's agent loop re-evaluates after each
/// step; this is the part of that idea which can be decided rather than hoped for.
/// </summary>
public sealed class PlanProgressTests
{
    private static PlanProgress WithAPlan()
    {
        var plan = new PlanProgress();
        plan.State(["Read the config", "Change the port", "Run the tests"]);
        return plan;
    }

    private static RoundVerdict Worked(PlanProgress plan) => plan.Round([true]);

    private static RoundVerdict Failed(PlanProgress plan) => plan.Round([false]);

    // ── Ticking forward ───────────────────────────────────────────────────

    [Fact]
    public void Nothing_is_planned_to_begin_with()
    {
        var plan = new PlanProgress();

        Assert.False(plan.HasPlan);
        Assert.Equal(0, plan.Done);
    }

    [Fact]
    public void A_round_that_worked_moves_the_plan_along()
    {
        var plan = WithAPlan();

        var verdict = Worked(plan);

        Assert.True(verdict.StepCompleted);
        Assert.Equal(1, plan.Done);
    }

    /// <summary>
    /// The defect. Every call in the round failed, and the strip moved anyway.
    /// </summary>
    [Fact]
    public void A_round_where_nothing_worked_is_not_a_step_done()
    {
        var plan = WithAPlan();

        var verdict = Failed(plan);

        Assert.False(verdict.StepCompleted);
        Assert.Equal(0, plan.Done);
    }

    /// <summary>
    /// A round is a mixture more often than not — read three files, one is
    /// missing. Something was learned, so it counts.
    /// </summary>
    [Fact]
    public void A_round_where_something_worked_counts()
    {
        var plan = WithAPlan();

        Assert.True(plan.Round([false, true, false]).StepCompleted);
    }

    /// <summary>
    /// A round with no calls is the model talking rather than acting — neither
    /// progress nor failure, and nothing to judge.
    /// </summary>
    [Fact]
    public void A_round_with_no_calls_is_neither()
    {
        var plan = WithAPlan();

        var verdict = plan.Round([]);

        Assert.False(verdict.StepCompleted);
        Assert.False(verdict.AskForRevision);
        Assert.False(verdict.ShouldStop);
        Assert.Equal(0, plan.ConsecutiveFailures);
    }

    [Fact]
    public void The_plan_never_ticks_past_its_own_end()
    {
        var plan = new PlanProgress();
        plan.State(["Only step"]);

        Worked(plan);
        var second = Worked(plan);

        Assert.False(second.StepCompleted);
        Assert.Equal(1, plan.Done);
    }

    // ── Rethinking ────────────────────────────────────────────────────────

    /// <summary>
    /// The first failure asks for a revision, not the third. By the third it has
    /// spent two rounds acting on something it already knew was wrong.
    /// </summary>
    [Fact]
    public void The_first_failed_round_asks_for_a_rethink()
    {
        var verdict = Failed(WithAPlan());

        Assert.True(verdict.AskForRevision);
        Assert.False(verdict.ShouldStop);
        Assert.Contains("state a new", verdict.Note!);
    }

    /// <summary>
    /// The words point at the plan rather than the error. The model can already
    /// see the failure in the tool results; what it cannot see is that the rest of
    /// its plan now rests on something that did not happen.
    /// </summary>
    [Fact]
    public void The_rethink_is_addressed_to_the_plan_not_the_error()
        => Assert.Contains("The rest of the plan assumed it did", Failed(WithAPlan()).Note!);

    /// <summary>With no plan stated there is no plan to revise, so it says less.</summary>
    [Fact]
    public void Without_a_plan_it_still_asks_what_now()
    {
        var verdict = Failed(new PlanProgress());

        Assert.True(verdict.AskForRevision);
        Assert.DoesNotContain("plan", verdict.Note!);
    }

    [Fact]
    public void A_revised_plan_starts_its_count_again()
    {
        var plan = WithAPlan();
        Worked(plan);

        var revised = plan.State(["Something else entirely", "And then this"]);

        Assert.True(revised);
        Assert.Equal(0, plan.Done);
        Assert.Equal(1, plan.Revisions);
    }

    /// <summary>
    /// Models restate things. Counting a repeat as a rewrite would stop a turn
    /// that is going perfectly well.
    /// </summary>
    [Fact]
    public void Restating_the_same_plan_is_not_a_revision()
    {
        var plan = WithAPlan();
        Worked(plan);

        var revised = plan.State(["Read the config", "Change the port", "Run the tests"]);

        Assert.False(revised);
        Assert.Equal(0, plan.Revisions);
        Assert.Equal(1, plan.Done);
    }

    [Fact]
    public void The_first_plan_is_not_a_revision()
    {
        var plan = new PlanProgress();

        Assert.False(plan.State(["One", "Two"]));
        Assert.Equal(0, plan.Revisions);
    }

    [Fact]
    public void An_empty_plan_changes_nothing()
    {
        var plan = WithAPlan();

        Assert.False(plan.State([]));
        Assert.Equal(3, plan.Steps.Count);
    }

    // ── Giving up ─────────────────────────────────────────────────────────

    /// <summary>
    /// The round cap would catch this eventually, but only after spending every
    /// remaining request discovering the same thing.
    /// </summary>
    [Fact]
    public void Enough_failures_in_a_row_and_it_stops()
    {
        var plan = WithAPlan();

        Assert.False(Failed(plan).ShouldStop);
        var second = Failed(plan);

        Assert.True(second.ShouldStop);
        Assert.Contains("rounds in a row failed", second.Note!);
    }

    /// <summary>
    /// A success in between means the failures were not in a row, and something is
    /// still being achieved.
    /// </summary>
    [Fact]
    public void A_success_resets_the_patience()
    {
        var plan = WithAPlan();

        Failed(plan);
        Worked(plan);

        Assert.Equal(0, plan.ConsecutiveFailures);
        Assert.False(Failed(plan).ShouldStop);
    }

    /// <summary>
    /// A plan rewritten this many times is not planning — it is a model going
    /// round in circles with a fresh list each lap.
    /// </summary>
    [Fact]
    public void A_plan_rewritten_too_often_stops_rather_than_thrashing()
    {
        var plan = WithAPlan();

        plan.State(["A"]);
        plan.State(["B"]);
        plan.State(["C"]);

        var verdict = Failed(plan);

        Assert.True(verdict.ShouldStop);
        Assert.Contains("rewritten", verdict.Note!);
    }

    /// <summary>
    /// Rewriting is only a problem when it is not working. A plan revised three
    /// times that keeps making progress is a model adapting, which is the whole
    /// point of letting it revise at all.
    /// </summary>
    [Fact]
    public void Rewriting_while_making_progress_does_not_stop_anything()
    {
        var plan = WithAPlan();

        plan.State(["A"]);
        plan.State(["B"]);
        plan.State(["C"]);

        Assert.False(Worked(plan).ShouldStop);
    }

    [Fact]
    public void Clearing_forgets_the_whole_turn()
    {
        var plan = WithAPlan();
        Failed(plan);
        plan.State(["Something else"]);

        plan.Clear();

        Assert.False(plan.HasPlan);
        Assert.Equal(0, plan.Done);
        Assert.Equal(0, plan.Revisions);
        Assert.Equal(0, plan.ConsecutiveFailures);
    }
}
