using System.Text.Json;
using Concierge.Shared.Caching;

namespace Concierge.Shared.Planning;

/// <summary>Bounded project/workspace catalogues for rendering. Execution, revisions and agent events stay live.</summary>
public sealed class CachedProjectStore(IProjectStore inner, TimeProvider? clock = null) : IProjectStore
{
    private readonly AsyncSnapshotCache<string, byte[]> _lists = new(3, 524_288, bytes => bytes.LongLength, clock: clock);
    public SnapshotCacheMetrics Metrics => _lists.Metrics;
    public async Task<IReadOnlyList<ConciergeProject>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var bytes = await _lists.GetAsync(includeArchived ? "all" : "active", TimeSpan.FromSeconds(1),
            async () => JsonSerializer.SerializeToUtf8Bytes(await inner.ListAsync(includeArchived, CancellationToken.None)), cancellationToken);
        return JsonSerializer.Deserialize<ConciergeProject[]>(bytes)!;
    }
    public async Task<IReadOnlyList<StartupJourney>> ListStartupsAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await _lists.GetAsync("startups", TimeSpan.FromSeconds(1),
            async () => JsonSerializer.SerializeToUtf8Bytes(await inner.ListStartupsAsync(CancellationToken.None)), cancellationToken);
        return JsonSerializer.Deserialize<StartupJourney[]>(bytes)!;
    }
    private async Task<T> Write<T>(Func<Task<T>> action)
    {
        _lists.Invalidate();
        try { return await action().ConfigureAwait(false); }
        finally { _lists.Invalidate(); }
    }
    public Task<ConciergeProject> CreateAsync(ConciergeProject project, CancellationToken cancellationToken = default) => Write(() => inner.CreateAsync(project, cancellationToken));
    public Task<ConciergeProject> SaveAsync(ConciergeProject project, CancellationToken cancellationToken = default) => Write(() => inner.SaveAsync(project, cancellationToken));
    public Task<StartupJourney> CreateStartupAsync(StartupJourney journey, CancellationToken cancellationToken = default) => Write(() => inner.CreateStartupAsync(journey, cancellationToken));
    public Task<StartupJourney> SaveStartupAsync(StartupJourney journey, CancellationToken cancellationToken = default) => Write(() => inner.SaveStartupAsync(journey, cancellationToken));
    public Task<ConciergeProject> CreateProjectForStartupAsync(Guid startupId, ConciergeProject project, CancellationToken cancellationToken = default)
        => Write(() => inner.CreateProjectForStartupAsync(startupId, project, cancellationToken));
    public Task<StartupJourney> LinkProjectToStartupAsync(Guid startupId, Guid projectId, CancellationToken cancellationToken = default)
        => Write(() => inner.LinkProjectToStartupAsync(startupId, projectId, cancellationToken));
    public Task<StartupJourney> LinkConversationToStartupAsync(Guid startupId, Guid conversationId, CancellationToken cancellationToken = default)
        => Write(() => inner.LinkConversationToStartupAsync(startupId, conversationId, cancellationToken));
    public Task<ConciergeProject?> GetAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
    public Task<StartupJourney?> GetStartupAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetStartupAsync(id, cancellationToken);
    public Task<IReadOnlyList<ProjectAgentEvent>> ReadAgentEventsAsync(Guid projectId, Guid runId, int afterSequence = -1, CancellationToken cancellationToken = default)
        => inner.ReadAgentEventsAsync(projectId, runId, afterSequence, cancellationToken);
}
