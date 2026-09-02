using System.Diagnostics;
using Concierge.Shared;

namespace Concierge.Tests;

/// <summary>
/// What a timeout policy must do (parity feature 12): decide how long a call may take from
/// outside the code that runs it, so a phone on battery and a desktop are not forced to
/// share one hardcoded ceiling.
/// </summary>
public sealed class ToolTimeoutPolicyTests : IDisposable
{
    private readonly string _workspace;

    public ToolTimeoutPolicyTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"concierge-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A killed child can hold a handle briefly; the directory is disposable.
        }
    }

    [Fact]
    public void A_policy_states_a_limit()
    {
        var policy = new FixedToolTimeoutPolicy(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), policy.TimeoutFor("run_command"));
    }

    [Fact]
    public void The_default_limit_matches_the_behaviour_it_replaced()
    {
        // The harness previously hardcoded five minutes. The default must not quietly
        // change that for hosts which never set a policy.
        Assert.Equal(TimeSpan.FromMinutes(5), ToolTimeoutPolicy.Default.TimeoutFor("run_command"));
    }

    [Fact]
    public async Task A_command_that_outlives_the_limit_is_stopped()
    {
        var harness = new AgentHarnessService(
            _workspace,
            publisher: null,
            timeouts: new FixedToolTimeoutPolicy(TimeSpan.FromSeconds(1)));

        var result = await harness.RunCommandAsync("cmd /c ping -n 30 127.0.0.1", approved: true);

        Assert.Equal(ConciergeToolOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_command_that_outlives_the_limit_is_stopped_promptly()
    {
        var harness = new AgentHarnessService(
            _workspace,
            publisher: null,
            timeouts: new FixedToolTimeoutPolicy(TimeSpan.FromSeconds(1)));

        var stopwatch = Stopwatch.StartNew();
        await harness.RunCommandAsync("cmd /c ping -n 30 127.0.0.1", approved: true);
        stopwatch.Stop();

        // Thirty pings take about half a minute. Finishing inside ten seconds proves the
        // limit was enforced rather than the command simply completing.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task A_command_inside_the_limit_still_succeeds()
    {
        var harness = new AgentHarnessService(
            _workspace,
            publisher: null,
            timeouts: new FixedToolTimeoutPolicy(TimeSpan.FromSeconds(30)));

        var result = await harness.RunCommandAsync("cmd /c echo hello", approved: true);

        Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task A_host_that_sets_no_policy_keeps_working()
    {
        var harness = new AgentHarnessService(_workspace);

        var result = await harness.RunCommandAsync("cmd /c echo hello", approved: true);

        Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
    }
}
