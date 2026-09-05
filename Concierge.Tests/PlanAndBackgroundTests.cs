using Concierge.Shared.Chat;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Long work: what it says it will do, and carrying on when you look away.
///
/// Both halves of the same problem. A run of six tool calls arrived as six
/// chips with no stated intent, so there was no way to tell from outside
/// whether it was halfway through something sensible or had been going in
/// circles since step two — and if you left the thread to find out, the run
/// was cancelled.
/// </summary>
public sealed class PlanAndBackgroundTests
{
    // ── The plan ──────────────────────────────────────────────────────────

    [Fact]
    public void A_stated_plan_is_lifted_out_of_the_reply()
    {
        var steps = PlanProtocol.Extract("""
            I will sort this out.

            ```plan
            ["Read the config", "Change the port", "Run the tests"]
            ```
            """);

        Assert.NotNull(steps);
        Assert.Equal(3, steps!.Count);
        Assert.Equal("Read the config", steps[0]);
    }

    [Fact]
    public void A_reply_with_no_plan_has_none()
    {
        Assert.Null(PlanProtocol.Extract("Just answering the question."));
        Assert.Null(PlanProtocol.Extract(string.Empty));
    }

    /// <summary>
    /// A model told to emit JSON will sometimes write a list. Being strict here
    /// buys nothing — the alternative to reading it is showing no plan at all.
    /// </summary>
    [Fact]
    public void A_plan_written_as_a_list_is_read_anyway()
    {
        var steps = PlanProtocol.Extract("""
            ```plan
            - Read the config
            - Change the port
            ```
            """);

        Assert.Equal(new[] { "Read the config", "Change the port" }, steps);
    }

    [Fact]
    public void A_numbered_plan_loses_its_numbers()
    {
        var steps = PlanProtocol.Extract("""
            ```plan
            1. Read the config
            2. Change the port
            ```
            """);

        Assert.Equal(new[] { "Read the config", "Change the port" }, steps);
    }

    /// <summary>
    /// A reply carrying two plans changed its mind mid-sentence; the first is
    /// the one it started from.
    /// </summary>
    [Fact]
    public void Only_the_first_plan_counts()
    {
        var steps = PlanProtocol.Extract("""
            ```plan
            ["First"]
            ```
            then
            ```plan
            ["Second"]
            ```
            """);

        Assert.Equal(new[] { "First" }, steps);
    }

    /// <summary>
    /// A malformed plan is no plan, not a failed turn. The work still happens
    /// and the chips still show what it did.
    /// </summary>
    [Fact]
    public void A_broken_plan_is_ignored_rather_than_thrown_over()
    {
        Assert.Null(PlanProtocol.Extract("""
            ```plan
            [ "unterminated
            ```
            """));
    }

    /// <summary>
    /// Forty steps is not a plan, and a thread full of it hides the
    /// conversation it is supposed to explain.
    /// </summary>
    [Fact]
    public void A_very_long_plan_is_cut_to_something_readable()
    {
        var many = string.Join(",", Enumerable.Range(1, 40).Select(i => $"\"Step {i}\""));

        var steps = PlanProtocol.Extract($"```plan\n[{many}]\n```");

        Assert.Equal(PlanProtocol.MaxSteps, steps!.Count);
    }

    [Fact]
    public void The_model_is_told_how_to_state_one()
    {
        Assert.Contains("```plan", PlanProtocol.SystemPromptAddendum);
        Assert.Contains("single-step task needs no plan", PlanProtocol.SystemPromptAddendum);
    }

    // ── Carrying on ───────────────────────────────────────────────────────

    [Fact]
    public void Nothing_is_running_to_begin_with()
    {
        var runs = new BackgroundRuns();

        Assert.Empty(runs.Running);
        Assert.False(runs.IsRunning(Guid.NewGuid()));
    }

    [Fact]
    public void A_started_turn_is_visible_from_anywhere()
    {
        var runs = new BackgroundRuns();
        var conversation = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        runs.Started(conversation, cancellation);

        Assert.True(runs.IsRunning(conversation));
        Assert.Contains(conversation, runs.Running);
        Assert.NotNull(runs.StartedAt(conversation));
    }

    [Fact]
    public void A_finished_turn_stops_being_visible()
    {
        var runs = new BackgroundRuns();
        var conversation = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        runs.Started(conversation, cancellation);
        runs.Finished(conversation);

        Assert.False(runs.IsRunning(conversation));
        Assert.Empty(runs.Running);
    }

    /// <summary>
    /// The other half of letting a run outlive its screen: without this, a run
    /// you walked away from could only be stopped by walking back to it.
    /// </summary>
    [Fact]
    public void A_run_can_be_stopped_from_somewhere_else()
    {
        var runs = new BackgroundRuns();
        var conversation = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        runs.Started(conversation, cancellation);
        runs.Stop(conversation);

        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void Stopping_something_that_is_not_running_does_nothing()
    {
        var runs = new BackgroundRuns();

        runs.Stop(Guid.NewGuid());
        runs.Finished(Guid.NewGuid());

        Assert.Empty(runs.Running);
    }

    /// <summary>
    /// A cancellation source disposed by the turn that owned it must not turn
    /// a stop into an exception on somebody else's thread.
    /// </summary>
    [Fact]
    public void Stopping_a_run_whose_source_has_gone_is_survivable()
    {
        var runs = new BackgroundRuns();
        var conversation = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();

        runs.Started(conversation, cancellation);
        cancellation.Dispose();

        runs.Stop(conversation);
    }

    [Fact]
    public void Everything_can_be_stopped_at_once()
    {
        var runs = new BackgroundRuns();
        var sources = Enumerable.Range(0, 3).Select(_ => new CancellationTokenSource()).ToList();

        foreach (var source in sources)
        {
            runs.Started(Guid.NewGuid(), source);
        }

        runs.StopAll();

        Assert.All(sources, source => Assert.True(source.IsCancellationRequested));

        foreach (var source in sources)
        {
            source.Dispose();
        }
    }

    /// <summary>A surface needs to know when to redraw.</summary>
    [Fact]
    public void Starting_and_finishing_both_announce_themselves()
    {
        var runs = new BackgroundRuns();
        var announcements = 0;
        runs.Changed += (_, _) => announcements++;

        var conversation = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        runs.Started(conversation, cancellation);
        runs.Finished(conversation);

        Assert.Equal(2, announcements);
    }
}
