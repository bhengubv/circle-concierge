using Concierge.Shared.Away;

namespace Concierge.Tests;

public sealed class AwayRequestLedgerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"away-receipts-{Guid.NewGuid():N}");

    [Fact]
    public async Task Completed_request_returns_the_original_answer_without_running_again()
    {
        var ledger = new AwayRequestLedger(_folder);
        var requestId = Guid.NewGuid();
        var calls = 0;

        var first = await ledger.ExecuteOnceAsync(requestId, "same-work", () =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new AwayAnswer(true, "Changed the page", "Done."));
        });
        var retry = await ledger.ExecuteOnceAsync(requestId, "same-work", () =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new AwayAnswer(false, "", "Must not run."));
        });

        Assert.Equal(1, calls);
        Assert.Equal(first, retry);
    }

    [Fact]
    public async Task Concurrent_delivery_of_one_request_executes_once()
    {
        var ledger = new AwayRequestLedger(_folder);
        var requestId = Guid.NewGuid();
        var calls = 0;

        async Task<AwayAnswer> Execute()
            => await ledger.ExecuteOnceAsync(requestId, "same-work", async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(30);
                return new AwayAnswer(true, "Changed the page", "Done.");
            });

        var answers = await Task.WhenAll(Execute(), Execute(), Execute());

        Assert.Equal(1, calls);
        Assert.All(answers, answer => Assert.Equal(answers[0], answer));
    }

    [Fact]
    public async Task Separate_ledger_hosts_serialize_the_same_request_id()
    {
        var firstLedger = new AwayRequestLedger(_folder);
        var secondLedger = new AwayRequestLedger(_folder);
        var requestId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var first = firstLedger.ExecuteOnceAsync(requestId, "same-work", async () =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
            return new AwayAnswer(true, "Saved once", "Done.");
        });
        await entered.Task;

        var second = secondLedger.ExecuteOnceAsync(requestId, "same-work", () =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new AwayAnswer(false, string.Empty, "Must not run."));
        });

        release.SetResult();
        var answers = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(answers[0], answers[1]);
    }

    [Fact]
    public async Task Completed_receipt_survives_a_new_ledger_instance()
    {
        var requestId = Guid.NewGuid();
        var expected = new AwayAnswer(true, "Saved", "Done.");
        await new AwayRequestLedger(_folder).ExecuteOnceAsync(requestId, "same-work", () => Task.FromResult(expected));

        var retried = await new AwayRequestLedger(_folder).ExecuteOnceAsync(requestId, "same-work",
            () => throw new InvalidOperationException("A completed request must not execute again."));

        Assert.Equal(expected, retried);
    }

    [Fact]
    public async Task Finished_answer_survives_the_watch_connection_dropping_after_acceptance()
    {
        using var connection = new CancellationTokenSource();
        var requestId = Guid.NewGuid();
        var expected = new AwayAnswer(true, "Answered", "The saved answer.");
        var calls = 0;
        var first = await new AwayRequestLedger(_folder).ExecuteOnceAsync(requestId, "same-work", () =>
        {
            calls++;
            connection.Cancel(); // HTTP disappears; accepted work has its own lifetime.
            return Task.FromResult(expected);
        }, connection.Token);
        var replay = await new AwayRequestLedger(_folder).ExecuteOnceAsync(requestId, "same-work", () =>
        {
            calls++;
            throw new InvalidOperationException("A finished answer must not execute again.");
        });
        Assert.Equal(expected, first);
        Assert.Equal(expected, replay);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task An_interrupted_request_is_not_replayed_after_restart()
    {
        var ledger = new AwayRequestLedger(_folder);
        var requestId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ExecuteOnceAsync(requestId, "same-work",
            () => throw new InvalidOperationException("simulated process interruption")));

        var replay = await new AwayRequestLedger(_folder).ExecuteOnceAsync(requestId, "same-work",
            () => throw new InvalidOperationException("must not duplicate the original side effect"));

        Assert.Contains("Check the conversation", replay.Reply);
    }

    [Fact]
    public async Task Reusing_an_id_for_different_work_is_rejected()
    {
        var ledger = new AwayRequestLedger(_folder);
        var requestId = Guid.NewGuid();
        await ledger.ExecuteOnceAsync(requestId, "first-work", () => Task.FromResult(new AwayAnswer(true, "", "Done.")));

        var replay = await ledger.ExecuteOnceAsync(requestId, "different-work",
            () => throw new InvalidOperationException("different work must not execute under this ID"));

        Assert.Contains("different work", replay.Reply);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
