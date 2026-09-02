using System.Collections.Concurrent;

namespace Concierge.Shared.Jobs;

/// <summary>Where a background job has got to.</summary>
public enum JobState
{
    /// <summary>Still working.</summary>
    Running = 0,

    /// <summary>Finished normally.</summary>
    Completed = 1,

    /// <summary>Stopped before it finished.</summary>
    Cancelled = 2,

    /// <summary>Ended in an error.</summary>
    Failed = 3,
}

/// <summary>What a job is doing and what it produced.</summary>
/// <param name="Id">The handle the caller was given.</param>
/// <param name="Description">What the job is for, in the caller's words.</param>
/// <param name="State">Where it has got to.</param>
/// <param name="Output">Its result, or the failure message when it ended badly.</param>
/// <param name="StartedAt">When it began.</param>
/// <param name="FinishedAt">When it ended, or null while it is still running.</param>
public sealed record JobStatus(
    string Id,
    string Description,
    JobState State,
    string Output,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Runs work in the background so a slow task does not hold a conversation open.
/// </summary>
/// <remarks>
/// The model starts the work, answers the user now, and collects the result on a later turn.
/// A failure is recorded as a failed job, never thrown back into the turn that started it —
/// otherwise one bad command takes the conversation with it.
/// </remarks>
public interface IJobRuntime
{
    /// <summary>Start work and return its handle immediately.</summary>
    string Start(string description, Func<CancellationToken, Task> work);

    /// <summary>Start work that produces text, and return its handle immediately.</summary>
    string Start(string description, Func<CancellationToken, Task<string>> work);

    /// <summary>What a job is doing, or null when the handle is unknown.</summary>
    JobStatus? Status(string id);

    /// <summary>Every job this runtime knows about, newest first.</summary>
    IReadOnlyList<JobStatus> List();

    /// <summary>Ask a job to stop. Unknown handles are ignored.</summary>
    void Stop(string id);

    /// <summary>Wait for a job to settle. Used by callers that need its result now.</summary>
    Task WaitAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Jobs held in this process.</summary>
public sealed class InMemoryJobRuntime : IJobRuntime
{
    private sealed class Entry(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>Assigned immediately after the task is created; the closure needs the
        /// entry to exist first so a job that finishes at once has somewhere to report.</summary>
        public Task Work { get; set; } = Task.CompletedTask;

        public JobStatus Status { get; set; } = null!;
    }

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();

    /// <inheritdoc />
    public string Start(string description, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Start(description, async token =>
        {
            await work(token).ConfigureAwait(false);
            return string.Empty;
        });
    }

    /// <inheritdoc />
    public string Start(string description, Func<CancellationToken, Task<string>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(work);

        var id = $"job-{Guid.NewGuid():N}";
        var cancellation = new CancellationTokenSource();
        var startedAt = DateTimeOffset.UtcNow;

        // The entry must exist before the work can settle, or a job that finishes
        // immediately would have nowhere to record its result.
        var entry = new Entry(cancellation)
        {
            Status = new JobStatus(id, description, JobState.Running, string.Empty, startedAt, null),
        };
        _jobs[id] = entry;

        var task = Task.Run(async () =>
        {
            try
            {
                var output = await work(cancellation.Token).ConfigureAwait(false);
                Settle(id, JobState.Completed, output ?? string.Empty);
            }
            catch (OperationCanceledException)
            {
                Settle(id, JobState.Cancelled, "Stopped before it finished.");
            }
            catch (Exception exception)
            {
                // A background failure belongs in the job record, never on the turn that
                // started it — one bad command must not end the conversation.
                Settle(id, JobState.Failed, exception.Message);
            }
        });

        entry.Work = task;
        return id;
    }

    /// <inheritdoc />
    public JobStatus? Status(string id)
        => _jobs.TryGetValue(id, out var entry) ? entry.Status : null;

    /// <inheritdoc />
    public IReadOnlyList<JobStatus> List()
        => _jobs.Values.Select(entry => entry.Status).OrderByDescending(status => status.StartedAt).ToList();

    /// <inheritdoc />
    public void Stop(string id)
    {
        if (_jobs.TryGetValue(id, out var entry))
        {
            entry.Cancellation.Cancel();
        }
    }

    /// <inheritdoc />
    public async Task WaitAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(id, out var entry))
        {
            return;
        }

        await entry.Work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Settle(string id, JobState state, string output)
    {
        if (_jobs.TryGetValue(id, out var entry))
        {
            entry.Status = entry.Status with
            {
                State = state,
                Output = output,
                FinishedAt = DateTimeOffset.UtcNow,
            };
        }
    }
}
