using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// A step is ticked off when it did what it said, not when something worked.
///
/// `PlanProgress.Round` ticked a step whenever *any* call in the round succeeded.
/// So a plan whose step was "write the config file" ticked when the model instead
/// listed a directory successfully — the strip advanced, somebody watching
/// believed the file had been written, and nothing had been. Same defect as an
/// approvals badge that always said two, on the surface whose whole job is showing
/// what is happening.
///
/// OpenMontage validates a stage's output before the pipeline may advance. This is
/// that idea at the size this product is: the promise is a file or a command, not
/// a JSON schema, so the plan strip stays something a person can read.
/// </summary>
public sealed class StepExpectationTests
{
    private static StepEvidence Ok(string tool, string? target = null) => new(tool, target, true);
    private static StepEvidence Failed(string tool, string? target = null) => new(tool, target, false);

    // ── What satisfies a promise ──────────────────────────────────────────

    [Fact]
    public void A_step_promising_nothing_is_satisfied_by_anything_that_worked()
        => Assert.True(StepExpectation.Unstated.SatisfiedBy([Ok("list_files")]));

    [Fact]
    public void Nothing_is_satisfied_by_a_round_where_every_call_failed()
    {
        Assert.False(StepExpectation.Unstated.SatisfiedBy([Failed("write_file", "a.txt")]));
        Assert.False(new StepExpectation(StepOutcome.FileWritten).SatisfiedBy([Failed("write_file", "a.txt")]));
    }

    [Fact]
    public void Nothing_is_satisfied_by_a_round_with_no_calls_at_all()
        => Assert.False(StepExpectation.Unstated.SatisfiedBy([]));

    [Fact]
    public void A_promise_to_write_is_not_kept_by_reading()
    {
        var promise = new StepExpectation(StepOutcome.FileWritten);

        Assert.False(promise.SatisfiedBy([Ok("read_file", "a.txt"), Ok("list_files")]));
        Assert.True(promise.SatisfiedBy([Ok("write_file", "a.txt")]));
    }

    [Fact]
    public void Editing_counts_as_writing()
        => Assert.True(new StepExpectation(StepOutcome.FileWritten).SatisfiedBy([Ok("edit_file", "a.txt")]));

    [Fact]
    public void A_promise_about_one_file_is_not_kept_by_writing_another()
    {
        var promise = new StepExpectation(StepOutcome.FileWritten, "appsettings.json");

        Assert.False(promise.SatisfiedBy([Ok("write_file", "notes.txt")]));
        Assert.True(promise.SatisfiedBy([Ok("write_file", "appsettings.json")]));
    }

    /// <summary>
    /// A model writes "appsettings.json" and the call reports an absolute path.
    /// Demanding equality would fail every real match and tick nothing, which is
    /// worse than the problem being fixed.
    /// </summary>
    [Fact]
    public void A_named_file_matches_the_full_path_it_was_actually_written_to()
        => Assert.True(new StepExpectation(StepOutcome.FileWritten, "appsettings.json")
            .SatisfiedBy([Ok("write_file", @"C:\work\src\appsettings.json")]));

    [Fact]
    public void A_promise_to_run_something_is_kept_by_running_it()
    {
        var promise = new StepExpectation(StepOutcome.CommandRan, "dotnet test");

        Assert.True(promise.SatisfiedBy([Ok("run_command", "dotnet test ./x.csproj")]));
        Assert.False(promise.SatisfiedBy([Ok("run_command", "git status")]));
    }

    [Fact]
    public void One_matching_call_among_several_is_enough()
        => Assert.True(new StepExpectation(StepOutcome.FileWritten, "a.txt")
            .SatisfiedBy([Ok("list_files"), Failed("run_command", "x"), Ok("write_file", "a.txt")]));

    // ── What the plan strip then shows ────────────────────────────────────

    [Fact]
    public void A_plan_with_no_expectations_behaves_exactly_as_it_did()
    {
        var plan = new PlanProgress();
        plan.State(["One", "Two"]);

        var verdict = plan.Judge([Ok("list_files")]);

        Assert.True(verdict.StepCompleted);
        Assert.Equal(1, plan.Done);
    }

    /// <summary>The defect, stated as a test.</summary>
    [Fact]
    public void A_round_that_did_something_else_does_not_tick_the_step_off()
    {
        var plan = new PlanProgress();
        plan.State(["Write the config file", "Run the tests"]);
        plan.Expect([
            new StepExpectation(StepOutcome.FileWritten, "appsettings.json"),
            new StepExpectation(StepOutcome.CommandRan, "dotnet test"),
        ]);

        var verdict = plan.Judge([Ok("list_files")]);

        Assert.False(verdict.StepCompleted);
        Assert.Equal(0, plan.Done);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Note));
    }

    [Fact]
    public void A_round_that_kept_the_promise_ticks_the_step_off()
    {
        var plan = new PlanProgress();
        plan.State(["Write the config file", "Run the tests"]);
        plan.Expect([
            new StepExpectation(StepOutcome.FileWritten, "appsettings.json"),
            new StepExpectation(StepOutcome.CommandRan, "dotnet test"),
        ]);

        Assert.True(plan.Judge([Ok("write_file", "appsettings.json")]).StepCompleted);
        Assert.Equal(1, plan.Done);

        Assert.True(plan.Judge([Ok("run_command", "dotnet test all")]).StepCompleted);
        Assert.Equal(2, plan.Done);
    }

    /// <summary>
    /// A round that worked is still a round that worked. Withholding the tick must
    /// not also start counting the turn as failing, or a plan whose steps are
    /// described loosely would stop itself.
    /// </summary>
    [Fact]
    public void Withholding_a_tick_does_not_count_as_a_failed_round()
    {
        var plan = new PlanProgress();
        plan.State(["Write the config file"]);
        plan.Expect([new StepExpectation(StepOutcome.FileWritten, "appsettings.json")]);

        var verdict = plan.Judge([Ok("list_files")]);

        Assert.False(verdict.ShouldStop);
        Assert.False(verdict.AskForRevision);
        Assert.Equal(0, plan.ConsecutiveFailures);
    }

    [Fact]
    public void A_round_where_everything_failed_still_asks_for_a_rethink()
    {
        var plan = new PlanProgress();
        plan.State(["Write the config file"]);
        plan.Expect([new StepExpectation(StepOutcome.FileWritten, "appsettings.json")]);

        var verdict = plan.Judge([Failed("write_file", "appsettings.json")]);

        Assert.True(verdict.AskForRevision);
        Assert.Equal(1, plan.ConsecutiveFailures);
    }

    [Fact]
    public void Restating_the_plan_forgets_the_old_promises()
    {
        var plan = new PlanProgress();
        plan.State(["Write the config file"]);
        plan.Expect([new StepExpectation(StepOutcome.FileWritten, "appsettings.json")]);
        plan.State(["Something else entirely"]);

        Assert.Equal(StepExpectation.Unstated, plan.CurrentExpectation);
        Assert.True(plan.Judge([Ok("list_files")]).StepCompleted);
    }

    [Fact]
    public void The_current_expectation_follows_the_step_in_progress()
    {
        var plan = new PlanProgress();
        plan.State(["One", "Two"]);
        plan.Expect([
            new StepExpectation(StepOutcome.FileWritten, "a.txt"),
            new StepExpectation(StepOutcome.CommandRan, "dotnet test"),
        ]);

        Assert.Equal(StepOutcome.FileWritten, plan.CurrentExpectation.Expected);

        plan.Judge([Ok("write_file", "a.txt")]);

        Assert.Equal(StepOutcome.CommandRan, plan.CurrentExpectation.Expected);
    }

    [Fact]
    public void More_steps_than_expectations_is_not_an_error()
    {
        var plan = new PlanProgress();
        plan.State(["One", "Two", "Three"]);
        plan.Expect([new StepExpectation(StepOutcome.FileWritten, "a.txt")]);

        Assert.True(plan.Judge([Ok("write_file", "a.txt")]).StepCompleted);

        // Step two promised nothing, so anything that worked ticks it.
        Assert.True(plan.Judge([Ok("list_files")]).StepCompleted);
        Assert.Equal(2, plan.Done);
    }

    // ── A design change is a promise too ──────────────────────────────────

    /// <summary>
    /// "Make it feel like a school newsletter" is a look, a heading and three
    /// sizes. Without a canvas outcome the strip could state those steps and then
    /// tick them off on any success at all — the behaviour `Judge` exists to stop,
    /// coming back through a medium it had never been taught about.
    /// </summary>
    [Fact]
    public void A_promise_to_change_the_design_is_kept_by_changing_it()
    {
        var promise = new StepExpectation(StepOutcome.CanvasChanged);

        Assert.True(promise.SatisfiedBy([Ok("design_add")]));
        Assert.True(promise.SatisfiedBy([Ok("design_look")]));
        Assert.True(promise.SatisfiedBy([Ok("design_change")]));
    }

    /// <summary>
    /// Looking at the canvas is not changing it, for the same reason reading a file
    /// does not keep a promise to write one.
    /// </summary>
    [Fact]
    public void Describing_the_canvas_does_not_keep_a_promise_to_change_it()
        => Assert.False(new StepExpectation(StepOutcome.CanvasChanged)
            .SatisfiedBy([Ok("design_describe")]));

    [Fact]
    public void A_file_tool_does_not_keep_a_promise_about_the_canvas()
        => Assert.False(new StepExpectation(StepOutcome.CanvasChanged)
            .SatisfiedBy([Ok("write_file", "a.txt")]));

    [Fact]
    public void A_design_step_can_name_what_it_will_touch()
    {
        var promise = new StepExpectation(StepOutcome.CanvasChanged, "look");

        Assert.True(promise.SatisfiedBy([Ok("design_look", "look")]));
        Assert.False(promise.SatisfiedBy([Ok("design_add", "heading")]));
    }

    [Fact]
    public void A_multi_step_design_change_ticks_off_as_it_goes()
    {
        var plan = new PlanProgress();
        plan.State(["Give it a newsletter look", "Add the masthead"]);
        plan.Expect([
            new StepExpectation(StepOutcome.CanvasChanged),
            new StepExpectation(StepOutcome.CanvasChanged),
        ]);

        // Looking first does not count as doing.
        Assert.False(plan.Judge([Ok("design_describe")]).StepCompleted);
        Assert.Equal(0, plan.Done);

        Assert.True(plan.Judge([Ok("design_look")]).StepCompleted);
        Assert.True(plan.Judge([Ok("design_add")]).StepCompleted);
        Assert.Equal(2, plan.Done);
    }
}
