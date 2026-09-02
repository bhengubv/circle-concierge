using Concierge.Shared.Delegation;

namespace Concierge.Tests;

/// <summary>
/// What delegation must do (parity features 18, 19, 23): hand a self-contained job to
/// something else, hear back, and keep going until the work is actually finished.
/// </summary>
/// <remarks>
/// The seam matters more here than in any other harness. Every other implementation of this
/// runs the child in the same process or on the same machine. Concierge has a mesh and
/// already publishes agent run logs across it, so "somewhere else" can mean another phone
/// that a person carried into signal.
/// </remarks>
public sealed class SubagentTests
{
    [Fact]
    public async Task A_delegated_job_comes_back_with_an_answer()
    {
        var runtime = RuntimeWith(("local", new EchoProvider()));

        var result = await runtime.DelegateAsync("local", "summarise this");

        Assert.True(result.Success);
        Assert.Contains("summarise this", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_is_not_registered_is_reported_not_thrown()
    {
        var runtime = RuntimeWith(("local", new EchoProvider()));

        var result = await runtime.DelegateAsync("on-the-mesh", "do a thing");

        Assert.False(result.Success);
        Assert.Contains("on-the-mesh", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_throws_becomes_a_failed_result()
    {
        var runtime = RuntimeWith(("broken", new ThrowingProvider()));

        var result = await runtime.DelegateAsync("broken", "do a thing");

        Assert.False(result.Success);
        Assert.Contains("it broke", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Registered_providers_can_be_listed()
    {
        var runtime = RuntimeWith(("local", new EchoProvider()), ("mesh", new EchoProvider()));

        Assert.Equal(["local", "mesh"], runtime.Providers.Order());
    }

    [Fact]
    public async Task An_empty_task_is_refused_before_a_provider_is_troubled()
    {
        var provider = new EchoProvider();
        var runtime = RuntimeWith(("local", provider));

        var result = await runtime.DelegateAsync("local", "   ");

        Assert.False(result.Success);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task A_child_reports_where_it_ran()
    {
        var runtime = RuntimeWith(("mesh", new EchoProvider()));

        var result = await runtime.DelegateAsync("mesh", "do a thing");

        Assert.Equal("mesh", result.ProviderName);
    }

    [Fact]
    public async Task Delegation_can_be_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var runtime = RuntimeWith(("local", new EchoProvider()));

        var result = await runtime.DelegateAsync("local", "do a thing", cancellation.Token);

        Assert.False(result.Success);
    }

    // ── Repeat until done ──────────────────────────────────────────────

    [Fact]
    public async Task Work_repeats_until_it_reports_itself_finished()
    {
        var attempts = 0;
        var runner = new IterativeRunner(maxRounds: 10);

        var rounds = await runner.RunUntilDoneAsync(_ =>
        {
            attempts++;
            return Task.FromResult(attempts >= 3);
        });

        Assert.Equal(3, rounds);
    }

    [Fact]
    public async Task Work_that_never_finishes_stops_at_the_round_limit()
    {
        var runner = new IterativeRunner(maxRounds: 4);

        var rounds = await runner.RunUntilDoneAsync(_ => Task.FromResult(false));

        Assert.Equal(4, rounds);
    }

    [Fact]
    public async Task Work_that_is_done_immediately_runs_once()
    {
        var runner = new IterativeRunner(maxRounds: 10);

        Assert.Equal(1, await runner.RunUntilDoneAsync(_ => Task.FromResult(true)));
    }

    [Fact]
    public async Task A_round_that_throws_stops_the_loop_rather_than_spinning()
    {
        // Repeating work that failed for a structural reason burns the round budget and
        // produces nothing; the caller needs the error, not four more attempts.
        var runner = new IterativeRunner(maxRounds: 10);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunUntilDoneAsync(_ => throw new InvalidOperationException("structural")));
    }

    [Fact]
    public async Task Cancelling_stops_the_rounds()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new IterativeRunner(maxRounds: 100);
        var rounds = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunUntilDoneAsync(_ =>
        {
            rounds++;
            cancellation.Cancel();
            return Task.FromResult(false);
        }, cancellation.Token));

        Assert.Equal(1, rounds);
    }

    [Fact]
    public void A_runner_with_no_rounds_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IterativeRunner(maxRounds: 0));
    }

    private static SubagentRuntime RuntimeWith(params (string Name, ISubagentProvider Provider)[] providers)
    {
        var runtime = new SubagentRuntime();
        foreach (var (name, provider) in providers)
        {
            runtime.RegisterProvider(name, provider);
        }

        return runtime;
    }

    private sealed class EchoProvider : ISubagentProvider
    {
        public int Calls { get; private set; }

        public Task<string> RunAsync(string task, CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult($"done: {task}");
        }
    }

    private sealed class ThrowingProvider : ISubagentProvider
    {
        public Task<string> RunAsync(string task, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("it broke");
    }
}
