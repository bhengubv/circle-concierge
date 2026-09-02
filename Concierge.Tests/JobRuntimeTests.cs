using Concierge.Shared.Jobs;

namespace Concierge.Tests;

/// <summary>
/// What background jobs must do (parity feature 17): let slow work start, hand back a
/// handle, and be collected later — instead of holding a turn open while a command runs.
/// </summary>
/// <remarks>
/// Without this, a build or a long search occupies the conversation for its whole duration
/// and a cancelled turn orphans the process. With it, the model starts the work, answers the
/// user, and picks the result up on a later turn.
/// </remarks>
public sealed class JobRuntimeTests
{
    [Fact]
    public async Task A_started_job_is_listed()
    {
        var jobs = new InMemoryJobRuntime();

        var id = jobs.Start("slow thing", _ => Task.Delay(50));

        Assert.Contains(jobs.List(), job => job.Id == id);
        await jobs.WaitAsync(id);
    }

    [Fact]
    public void A_started_job_is_running_before_it_finishes()
    {
        var jobs = new InMemoryJobRuntime();
        var release = new TaskCompletionSource();

        var id = jobs.Start("blocked", _ => release.Task);

        Assert.Equal(JobState.Running, jobs.Status(id)!.State);
        release.SetResult();
    }

    [Fact]
    public async Task A_finished_job_reports_that_it_finished()
    {
        var jobs = new InMemoryJobRuntime();
        var id = jobs.Start("quick", _ => Task.CompletedTask);

        await jobs.WaitAsync(id);

        Assert.Equal(JobState.Completed, jobs.Status(id)!.State);
    }

    [Fact]
    public async Task A_job_keeps_what_it_produced()
    {
        var jobs = new InMemoryJobRuntime();
        var id = jobs.Start("produces output", async _ =>
        {
            await Task.Yield();
            return "THE-OUTPUT";
        });

        await jobs.WaitAsync(id);

        Assert.Equal("THE-OUTPUT", jobs.Status(id)!.Output);
    }

    [Fact]
    public async Task A_job_that_throws_is_failed_not_lost()
    {
        var jobs = new InMemoryJobRuntime();
        var id = jobs.Start("breaks", _ => throw new InvalidOperationException("it broke"));

        await jobs.WaitAsync(id);

        var status = jobs.Status(id)!;
        Assert.Equal(JobState.Failed, status.State);
        Assert.Contains("it broke", status.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_that_throws_does_not_bring_down_the_caller()
    {
        var jobs = new InMemoryJobRuntime();

        var id = jobs.Start("breaks", _ => throw new InvalidOperationException("it broke"));
        await jobs.WaitAsync(id);

        // Reaching here at all is the assertion: Start must not rethrow on the caller's turn.
        Assert.Equal(JobState.Failed, jobs.Status(id)!.State);
    }

    [Fact]
    public async Task A_stopped_job_is_cancelled()
    {
        var jobs = new InMemoryJobRuntime();
        var id = jobs.Start("long", token => Task.Delay(Timeout.Infinite, token));

        jobs.Stop(id);
        await jobs.WaitAsync(id);

        Assert.Equal(JobState.Cancelled, jobs.Status(id)!.State);
    }

    [Fact]
    public void Stopping_a_job_that_does_not_exist_is_harmless()
    {
        new InMemoryJobRuntime().Stop("no-such-job");
    }

    [Fact]
    public void Asking_about_a_job_that_does_not_exist_returns_nothing()
    {
        Assert.Null(new InMemoryJobRuntime().Status("no-such-job"));
    }

    [Fact]
    public async Task Each_job_gets_its_own_identity()
    {
        var jobs = new InMemoryJobRuntime();

        var first = jobs.Start("one", _ => Task.CompletedTask);
        var second = jobs.Start("two", _ => Task.CompletedTask);

        Assert.NotEqual(first, second);
        await jobs.WaitAsync(first);
        await jobs.WaitAsync(second);
    }

    [Fact]
    public async Task A_job_remembers_what_it_was_for()
    {
        var jobs = new InMemoryJobRuntime();
        var id = jobs.Start("rebuild the index", _ => Task.CompletedTask);

        await jobs.WaitAsync(id);

        Assert.Equal("rebuild the index", jobs.Status(id)!.Description);
    }
}
