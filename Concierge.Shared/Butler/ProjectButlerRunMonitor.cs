using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Concierge.Shared.Planning;

namespace Concierge.Shared.Butler;

/// <summary>Reconciles active Butler operations into their owning durable Concierge Projects.</summary>
public sealed class ProjectButlerRunMonitor(
    IProjectStore projects,
    IButlerWorkRunApi butler,
    ILogger<ProjectButlerRunMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CancelledDispatchReconciliationWindow = TimeSpan.FromSeconds(30);
    public event EventHandler? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileSafelyAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await ReconcileSafelyAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task ReconcileSafelyAsync(CancellationToken cancellationToken)
    {
        try { await ReconcileOnceAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "The Butler run monitor pass failed; it will retry on the next interval.");
        }
    }

    /// <summary>One reconciliation pass, also exposed for deterministic host and recovery checks.</summary>
    public async Task ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await projects.ListAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);
        foreach (var project in snapshot)
        {
            foreach (var run in project.ButlerRuns.Where(run => !IsTerminal(run.Status)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var current = run;
                    if (current.ButlerRunId == Guid.Empty)
                    {
                        var recovered = await butler.FindByIdempotencyKeyAsync(current.IdempotencyKey, cancellationToken)
                            .ConfigureAwait(false);
                        if (recovered is null && current.CancellationRequested)
                        {
                            // A POST may still be committing at Butler after the caller process
                            // exits. Keep checking its idempotency key briefly before closing the
                            // local intent so a late acceptance can still be cancelled.
                            if (DateTimeOffset.UtcNow - current.UpdatedAt < CancelledDispatchReconciliationWindow)
                                continue;
                            await MutateRunAsync(project.Id, current.Id, item => item with
                            {
                                Status = "cancelled",
                                RequestJson = null,
                                UpdatedAt = DateTimeOffset.UtcNow,
                            }, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        if (recovered is null)
                        {
                            var request = JsonSerializer.Deserialize<ButlerWorkRunRequest>(current.RequestJson!,
                                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                                ?? throw new JsonException("The saved Butler dispatch intent was empty.");
                            var accepted = await butler.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
                            await MutateRunAsync(project.Id, current.Id, item => item with
                            {
                                ButlerRunId = accepted.ButlerRunId,
                                RequestJson = null,
                                Status = item.CancellationRequested ? "cancellation_requested" : accepted.Status,
                                UpdatedAt = DateTimeOffset.UtcNow,
                            }, cancellationToken).ConfigureAwait(false);
                            current = (await projects.GetAsync(project.Id, cancellationToken).ConfigureAwait(false))!
                                .ButlerRuns.Single(item => item.Id == current.Id);
                            if (current.CancellationRequested)
                                await butler.CancelAsync(accepted.ButlerRunId, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await MutateRunAsync(project.Id, current.Id, item => item with
                            {
                                ButlerRunId = recovered.Run.Id,
                                RequestJson = null,
                                UpdatedAt = DateTimeOffset.UtcNow,
                            }, cancellationToken).ConfigureAwait(false);
                            current = (await projects.GetAsync(project.Id, cancellationToken).ConfigureAwait(false))!
                                .ButlerRuns.Single(item => item.Id == current.Id);
                            if (current.CancellationRequested)
                                await butler.CancelAsync(recovered.Run.Id, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    var envelope = await butler.GetRunAsync(current.ButlerRunId, cancellationToken).ConfigureAwait(false);
                    var events = await butler.ReadEventsAsync(current.ButlerRunId, current.LastEventSequence, cancellationToken)
                        .ConfigureAwait(false);
                    await PersistRemoteStateAsync(project.Id, current.Id, envelope.Run.Status,
                        envelope.Run.CancellationRequested, envelope.Run.Result?.GetRawText(), envelope.Run.Problem,
                        events, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException
                    or InvalidOperationException or OperationCanceledException or JsonException)
                {
                    logger.LogWarning(exception,
                        "Could not refresh Butler run {ButlerRunId} for Project {ProjectId}; retaining its last confirmed state.",
                        run.ButlerRunId, project.Id);
                    try
                    {
                        await PersistMonitorErrorAsync(project.Id, run.Id, exception.Message, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
                    {
                        logger.LogError(persistenceException,
                            "Could not persist the monitor error for Project run {ProjectRunId}.", run.Id);
                    }
                }
            }
        }
    }

    private async Task PersistRemoteStateAsync(Guid projectId, Guid projectRunId, string status,
        bool cancellationRequested,
        string? resultJson, string? problem, IReadOnlyList<ButlerWorkRunEvent> events,
        CancellationToken cancellationToken)
    {
        await MutateRunAsync(projectId, projectRunId, run =>
        {
            var merged = MergeEvents(run.Events, events);
            var sequence = merged.Count == 0 ? run.LastEventSequence : Math.Max(run.LastEventSequence, merged[^1].Sequence);
            if (run.Status.Equals(status, StringComparison.OrdinalIgnoreCase)
                && run.CancellationRequested == cancellationRequested
                && run.ResultJson == resultJson && run.Problem == problem
                && run.LastEventSequence == sequence && run.LastMonitorError is null)
                return run;

            return run with
            {
                Status = status,
                CancellationRequested = cancellationRequested,
                ResultJson = resultJson,
                Problem = problem,
                LastEventSequence = sequence,
                Events = merged,
                LastMonitorError = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistMonitorErrorAsync(Guid projectId, Guid projectRunId, string message,
        CancellationToken cancellationToken)
    {
        await MutateRunAsync(projectId, projectRunId, run => run.LastMonitorError == message
            ? run
            : run with { LastMonitorError = message, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task MutateRunAsync(Guid projectId, Guid projectRunId,
        Func<ProjectButlerRun, ProjectButlerRun> update, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var project = await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (project is null) return;
            var current = project.ButlerRuns.SingleOrDefault(item => item.Id == projectRunId);
            if (current is null) return;
            var changed = update(current);
            if (ReferenceEquals(changed, current)) return;
            var latestRuns = project.ButlerRuns.Select(item => item.Id == projectRunId ? changed : item).ToArray();
            try
            {
                await projects.SaveAsync(project with { ButlerRuns = latestRuns }, cancellationToken).ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (ProjectRevisionConflictException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new ProjectRevisionConflictException(projectId);
    }

    private static bool IsTerminal(string status)
        => status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProjectButlerEvent> MergeEvents(
        IReadOnlyList<ProjectButlerEvent> existing, IReadOnlyList<ButlerWorkRunEvent> incoming)
    {
        var bySequence = existing.ToDictionary(item => item.Sequence);
        foreach (var item in incoming)
            bySequence[item.Sequence] = new ProjectButlerEvent(item.Sequence, item.Type,
                item.Data.GetRawText(), item.CreatedAt);
        return bySequence.Values.OrderBy(item => item.Sequence).ToArray();
    }
}
