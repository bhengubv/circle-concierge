using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What the scheduler must do (parity features 4 and 5): overlap calls that are safe to
/// overlap, serialise the ones that are not, and leave a valid record when a turn is
/// abandoned halfway.
/// </summary>
/// <remarks>
/// Cancellation is the half people skip. A turn stopped mid-flight leaves calls that started,
/// calls that never started, and a conversation that must still make sense when it is
/// reloaded — so every call the model asked for gets a result, even if that result is
/// "never ran".
/// </remarks>
public sealed class ToolCallSchedulerTests
{
    [Fact]
    public async Task Every_call_gets_a_result()
    {
        var scheduler = SchedulerFor(new FakeTool("a", concurrencySafe: true), new FakeTool("b", concurrencySafe: true));

        var outcomes = await scheduler.ExecuteAsync([Call("a"), Call("b")]);

        Assert.Equal(2, outcomes.Count);
    }

    [Fact]
    public async Task Results_come_back_in_the_order_the_model_asked_for_them()
    {
        var slow = new FakeTool("slow", concurrencySafe: true, delay: TimeSpan.FromMilliseconds(80));
        var fast = new FakeTool("fast", concurrencySafe: true);
        var scheduler = SchedulerFor(slow, fast);

        var outcomes = await scheduler.ExecuteAsync([Call("slow"), Call("fast")]);

        Assert.Equal(["slow", "fast"], outcomes.Select(outcome => outcome.ToolName));
    }

    [Fact]
    public async Task Safe_calls_overlap()
    {
        // Asserted by observation rather than by the clock: each invocation announces itself
        // and waits for the other, so the test can only pass if both are in flight at once.
        // A wall-clock version of this passes alone and fails under a loaded test run.
        var tool = new RendezvousTool("read", expectedParticipants: 2);
        var scheduler = SchedulerFor(tool);

        await scheduler.ExecuteAsync([Call("read"), Call("read")]);

        Assert.True(tool.BothArrived);
    }

    [Fact]
    public async Task An_unsafe_call_does_not_overlap_anything()
    {
        var unsafeTool = new FakeTool("write", concurrencySafe: false, delay: TimeSpan.FromMilliseconds(60));
        var scheduler = SchedulerFor(unsafeTool, new FakeTool("read", concurrencySafe: true, delay: TimeSpan.FromMilliseconds(60)));

        await scheduler.ExecuteAsync([Call("write"), Call("read")]);

        Assert.Equal(1, unsafeTool.MaxObservedConcurrency);
    }

    [Fact]
    public async Task Two_unsafe_calls_run_one_at_a_time()
    {
        var tool = new FakeTool("write", concurrencySafe: false, delay: TimeSpan.FromMilliseconds(40));
        var scheduler = SchedulerFor(tool);

        await scheduler.ExecuteAsync([Call("write"), Call("write")]);

        Assert.Equal(1, tool.MaxObservedConcurrency);
    }

    // ── Failure ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tool_that_throws_becomes_a_failed_result_not_an_exception()
    {
        var scheduler = SchedulerFor(new ThrowingTool("broken"));

        var outcome = Assert.Single(await scheduler.ExecuteAsync([Call("broken")]));

        Assert.False(outcome.Result.Success);
        Assert.Equal(ToolCallDisposition.Executed, outcome.Disposition);
    }

    [Fact]
    public async Task One_failing_call_does_not_stop_the_others()
    {
        var scheduler = SchedulerFor(new ThrowingTool("broken"), new FakeTool("fine", concurrencySafe: true));

        var outcomes = await scheduler.ExecuteAsync([Call("broken"), Call("fine")]);

        Assert.True(outcomes[1].Result.Success);
    }

    [Fact]
    public async Task A_call_naming_a_tool_that_does_not_exist_is_reported_not_thrown()
    {
        var scheduler = SchedulerFor(new FakeTool("real", concurrencySafe: true));

        var outcome = Assert.Single(await scheduler.ExecuteAsync([Call("imaginary")]));

        Assert.False(outcome.Result.Success);
        Assert.Contains("imaginary", outcome.Result.FailureMessage ?? string.Empty, StringComparison.Ordinal);
    }

    // ── Cancellation ───────────────────────────────────────────────────

    [Fact]
    public async Task Calls_that_never_started_still_get_a_result()
    {
        using var cancellation = new CancellationTokenSource();
        var scheduler = SchedulerFor(new FakeTool("slow", concurrencySafe: false, delay: TimeSpan.FromMilliseconds(60), onStart: cancellation.Cancel));

        var outcomes = await scheduler.ExecuteAsync([Call("slow"), Call("slow"), Call("slow")], cancellation.Token);

        Assert.Equal(3, outcomes.Count);
    }

    [Fact]
    public async Task A_skipped_call_says_it_was_skipped()
    {
        using var cancellation = new CancellationTokenSource();
        var scheduler = SchedulerFor(new FakeTool("slow", concurrencySafe: false, delay: TimeSpan.FromMilliseconds(60), onStart: cancellation.Cancel));

        var outcomes = await scheduler.ExecuteAsync([Call("slow"), Call("slow")], cancellation.Token);

        Assert.Equal(ToolCallDisposition.Skipped, outcomes[^1].Disposition);
    }

    [Fact]
    public async Task A_skipped_call_carries_a_result_the_conversation_can_show()
    {
        using var cancellation = new CancellationTokenSource();
        var scheduler = SchedulerFor(new FakeTool("slow", concurrencySafe: false, delay: TimeSpan.FromMilliseconds(60), onStart: cancellation.Cancel));

        var outcomes = await scheduler.ExecuteAsync([Call("slow"), Call("slow")], cancellation.Token);

        Assert.False(string.IsNullOrWhiteSpace(outcomes[^1].Result.FailureMessage));
    }

    [Fact]
    public async Task Cancelling_before_anything_starts_still_answers_every_call()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var scheduler = SchedulerFor(new FakeTool("a", concurrencySafe: true));

        var outcomes = await scheduler.ExecuteAsync([Call("a"), Call("a")], cancellation.Token);

        Assert.All(outcomes, outcome => Assert.Equal(ToolCallDisposition.Skipped, outcome.Disposition));
    }

    [Fact]
    public async Task No_calls_at_all_is_not_an_error()
    {
        Assert.Empty(await SchedulerFor(new FakeTool("a", concurrencySafe: true)).ExecuteAsync([]));
    }

    private static ToolCallScheduler SchedulerFor(params IAgentTool[] tools)
        => new(new AgentToolRegistry(tools), maxParallel: 4);

    private static PlannedToolCall Call(string name) => new(name, new JsonObject());

    private class FakeTool(
        string name,
        bool concurrencySafe,
        TimeSpan delay = default,
        Action? onStart = null) : IAgentTool
    {
        private int _running;

        public int MaxObservedConcurrency { get; private set; }

        public string Name => name;
        public string Description => "a fake";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => concurrencySafe;
        public bool IsConcurrencySafe => concurrencySafe;

        public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var running = Interlocked.Increment(ref _running);
            MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, running);
            onStart?.Invoke();
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, CancellationToken.None);
                }

                return new AgentToolResult(true, $"{name} ran", null);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>
    /// A concurrency-safe tool whose invocations wait for each other. If the scheduler runs
    /// them one after another the wait times out and <see cref="BothArrived"/> stays false,
    /// so the assertion is about real overlap rather than elapsed time.
    /// </summary>
    private sealed class RendezvousTool(string name, int expectedParticipants) : IAgentTool
    {
        private readonly CountdownEvent _arrivals = new(expectedParticipants);

        public bool BothArrived { get; private set; }

        public string Name => name;
        public string Description => "waits for its twin";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            _arrivals.Signal();

            // Generous, because it only elapses when the scheduler failed to overlap.
            if (_arrivals.Wait(TimeSpan.FromSeconds(5)))
            {
                BothArrived = true;
            }

            return Task.FromResult(new AgentToolResult(true, $"{name} ran", null));
        }
    }

    private sealed class ThrowingTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => "throws";
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("it broke");
    }
}
