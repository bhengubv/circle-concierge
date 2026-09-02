using Concierge.Shared.Delegation;

namespace Concierge.Tests;

/// <summary>
/// What workflows must do (parity feature 20): sequence work deterministically, so a job with
/// several steps does not depend on the model remembering what it was doing.
/// </summary>
/// <remarks>
/// The smaller the model, the more this matters. A capable model can improvise a five-step
/// job; a 4B model on a phone loses its place by step three. A workflow puts the order in
/// code, and leaves the model to do one well-defined thing at a time.
/// </remarks>
public sealed class WorkflowTests
{
    [Fact]
    public async Task Steps_run_in_order()
    {
        var order = new List<string>();
        var workflow = new Workflow("three steps")
            .Then("first", _ => { order.Add("first"); return Task.FromResult("a"); })
            .Then("second", _ => { order.Add("second"); return Task.FromResult("b"); })
            .Then("third", _ => { order.Add("third"); return Task.FromResult("c"); });

        await workflow.RunAsync();

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public async Task A_step_sees_what_the_last_one_produced()
    {
        var workflow = new Workflow("hand along")
            .Then("first", _ => Task.FromResult("the value"))
            .Then("second", context => Task.FromResult($"got: {context.Previous}"));

        var result = await workflow.RunAsync();

        Assert.Equal("got: the value", result.Output);
    }

    [Fact]
    public async Task A_step_can_read_anything_an_earlier_step_produced()
    {
        var workflow = new Workflow("look back")
            .Then("first", _ => Task.FromResult("one"))
            .Then("second", _ => Task.FromResult("two"))
            .Then("third", context => Task.FromResult(context.ResultOf("first") ?? "missing"));

        Assert.Equal("one", (await workflow.RunAsync()).Output);
    }

    [Fact]
    public async Task The_last_step_decides_the_answer()
    {
        var workflow = new Workflow("answer")
            .Then("first", _ => Task.FromResult("ignored"))
            .Then("last", _ => Task.FromResult("the answer"));

        Assert.Equal("the answer", (await workflow.RunAsync()).Output);
    }

    [Fact]
    public async Task A_failing_step_stops_the_workflow()
    {
        var reached = false;
        var workflow = new Workflow("stops")
            .Then("breaks", _ => throw new InvalidOperationException("it broke"))
            .Then("never", _ => { reached = true; return Task.FromResult("x"); });

        await workflow.RunAsync();

        Assert.False(reached);
    }

    [Fact]
    public async Task A_failing_step_says_which_step_failed()
    {
        var workflow = new Workflow("names the failure")
            .Then("the-bad-step", _ => throw new InvalidOperationException("it broke"));

        var result = await workflow.RunAsync();

        Assert.False(result.Success);
        Assert.Contains("the-bad-step", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_step_keeps_what_earlier_steps_produced()
    {
        // Half a workflow's output is still worth having, and it is what tells you where
        // things actually went wrong.
        var workflow = new Workflow("partial")
            .Then("worked", _ => Task.FromResult("kept"))
            .Then("broke", _ => throw new InvalidOperationException("it broke"));

        var result = await workflow.RunAsync();

        Assert.Equal("kept", result.Steps["worked"]);
    }

    [Fact]
    public async Task A_workflow_with_no_steps_is_not_an_error()
    {
        var result = await new Workflow("empty").RunAsync();

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task A_workflow_can_be_cancelled_between_steps()
    {
        using var cancellation = new CancellationTokenSource();
        var reached = false;
        var workflow = new Workflow("cancelled")
            .Then("first", _ => { cancellation.Cancel(); return Task.FromResult("a"); })
            .Then("second", _ => { reached = true; return Task.FromResult("b"); });

        await workflow.RunAsync(cancellation.Token);

        Assert.False(reached);
    }

    [Fact]
    public void A_step_without_a_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new Workflow("x").Then("  ", _ => Task.FromResult("y")));
    }

    [Fact]
    public void Two_steps_cannot_share_a_name()
    {
        // Results are looked up by name, so a duplicate would silently shadow the earlier one.
        var workflow = new Workflow("x").Then("same", _ => Task.FromResult("a"));

        Assert.Throws<ArgumentException>(() => workflow.Then("same", _ => Task.FromResult("b")));
    }
}
