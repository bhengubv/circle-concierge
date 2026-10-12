using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Concierge.Shared.Butler;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Planning;

/// <summary>Lifecycle for a goal that owns one or more workflows.</summary>
public enum ProjectStatus
{
    Planning,
    Active,
    Blocked,
    AwaitingPerson,
    Review,
    Completed,
    Archived,
}

/// <summary>Progress state for a workflow or one of its steps.</summary>
public enum ProjectWorkStatus
{
    Planned,
    Active,
    Blocked,
    AwaitingPerson,
    Review,
    Completed,
    Skipped,
}

/// <summary>The opening branch for a business-level Start-Up Mode journey.</summary>
public enum BusinessJourneyType
{
    NewBusiness,
    ExistingBusiness,
}

/// <summary>
/// A durable business-level goal that can connect multiple Concierge projects.
/// The journey stores the person's business context; projects retain their own workflows.
/// </summary>
public sealed record StartupJourney
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public BusinessJourneyType Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Goal { get; init; } = string.Empty;
    public string CurrentContext { get; init; } = string.Empty;
    public ProjectStatus Status { get; init; } = ProjectStatus.Planning;
    public IReadOnlyList<Guid> ProjectIds { get; init; } = [];
    public IReadOnlyList<Guid> ConversationIds { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Revision { get; init; }

    public static StartupJourney Create(BusinessJourneyType type, string name, string goal, string currentContext = "")
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        if (type == BusinessJourneyType.ExistingBusiness && string.IsNullOrWhiteSpace(currentContext))
            throw new ArgumentException("Describe the existing business before creating its Start-Up Mode journey.", nameof(currentContext));

        var now = DateTimeOffset.UtcNow;
        return new StartupJourney
        {
            Type = type,
            Name = name.Trim(),
            Goal = goal.Trim(),
            CurrentContext = currentContext.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}

/// <summary>A durable outcome with the workflow, decisions, assumptions, and artifacts that support it.</summary>
public sealed record ConciergeProject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string DesiredOutcome { get; init; } = string.Empty;
    public string WorkType { get; init; } = "General";
    /// <summary>Capability the selected starter example is designed to showcase; informational, not an agent execution claim.</summary>
    public string? StarterAgentFocus { get; init; }
    public Guid? StartupJourneyId { get; init; }
    public ProjectStatus Status { get; init; } = ProjectStatus.Planning;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Optimistic concurrency version, assigned by <see cref="IProjectStore"/>.</summary>
    public int Revision { get; init; }
    public decimal? BudgetLimit { get; init; }
    public DateTimeOffset? TargetDate { get; init; }
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<Guid> ConversationIds { get; init; } = [];
    public IReadOnlyList<ProjectWorkflow> Workflows { get; init; } = [];
    public IReadOnlyList<ProjectWorkspace> Workspaces { get; init; } = [];
    public IReadOnlyList<ProjectDecision> Decisions { get; init; } = [];
    public IReadOnlyList<ProjectArtifact> Artifacts { get; init; } = [];
    /// <summary>Analysis-only agent work owned by this Project and retained with its history.</summary>
    public IReadOnlyList<ProjectAgentRun> AgentRuns { get; init; } = [];
    /// <summary>Durable request identities let a retry recover a run even when its caller restarted.</summary>
    public IReadOnlyList<ProjectAgentRequest> AgentRequests { get; init; } = [];
    /// <summary>External operations dispatched to Butler, retained so Concierge can resume monitoring after a turn or restart.</summary>
    public IReadOnlyList<ProjectButlerRun> ButlerRuns { get; init; } = [];

    /// <summary>Build a project from a person's desired result.</summary>
    public static ConciergeProject Create(string name, string desiredOutcome, string workType = "General")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredOutcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(workType);
        var now = DateTimeOffset.UtcNow;
        return new ConciergeProject
        {
            Name = name.Trim(),
            DesiredOutcome = desiredOutcome.Trim(),
            WorkType = workType.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}

/// <summary>A separately trackable stream of work inside a project.</summary>
public sealed record ProjectWorkflow
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string DesiredOutcome { get; init; } = string.Empty;
    public ProjectWorkStatus Status { get; init; } = ProjectWorkStatus.Planned;
    public IReadOnlyList<ProjectStep> Steps { get; init; } = [];
}

/// <summary>A focused area of work within a Project, linked to its workflows and conversations.</summary>
public sealed record ProjectWorkspace
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string DesiredOutcome { get; init; } = string.Empty;
    public ProjectWorkStatus Status { get; init; } = ProjectWorkStatus.Planned;
    public IReadOnlyList<Guid> WorkflowIds { get; init; } = [];
    public IReadOnlyList<Guid> ConversationIds { get; init; } = [];
    public IReadOnlyList<Guid> ArtifactIds { get; init; } = [];
}

/// <summary>A milestone or task in a workflow. Dependencies are stable step IDs.</summary>
public sealed record ProjectStep
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ProjectWorkStatus Status { get; init; } = ProjectWorkStatus.Planned;
    public IReadOnlyList<Guid> DependsOn { get; init; } = [];
    public IReadOnlyList<Guid> ArtifactIds { get; init; } = [];
}

/// <summary>A choice made while shaping or carrying out the project.</summary>
public sealed record ProjectDecision
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid? ConversationId { get; init; }
}

/// <summary>A durable output or reference attached to the project.</summary>
public sealed record ProjectArtifact
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string MediaType { get; init; } = "application/octet-stream";
    public string Location { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public Guid? ConversationId { get; init; }
    public Guid? WorkflowId { get; init; }
}

/// <summary>A bounded, reviewable analysis task run for a Project.</summary>
public sealed record ProjectAgentRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Fingerprint of the caller's intent, used to reject an ID reused for different work.</summary>
    public string RequestFingerprint { get; init; } = string.Empty;
    public string Name { get; init; } = "Analysis agent";
    public string Task { get; init; } = string.Empty;
    public ProjectAgentStatus Status { get; init; } = ProjectAgentStatus.Queued;
    public string Output { get; init; } = string.Empty;
    public string? Problem { get; init; }
    public string RuntimeId { get; init; } = "circleai";
    public Guid? ConversationId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public IReadOnlyList<ProjectAgentEvent> Events { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>An immutable, cursor-addressable change in a Project-agent run.</summary>
public sealed record ProjectAgentEvent(
    Guid Id,
    int Sequence,
    ProjectAgentEventType Type,
    string Data,
    DateTimeOffset CreatedAt);

public enum ProjectAgentEventType
{
    Queued,
    Started,
    Output,
    ReviewReady,
    Paused,
    Failed,
}

/// <summary>A durable idempotency key for a Project-agent request, retained across app restarts.</summary>
public sealed record ProjectAgentRequest
{
    public string Fingerprint { get; init; } = string.Empty;
    public Guid RunId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A Butler-owned operation tracked within its Concierge Project.</summary>
public sealed record ProjectButlerRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConciergeRunId { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    /// <summary>Serialized dispatch intent retained until Butler's durable run identity is confirmed.</summary>
    public string? RequestJson { get; init; }
    public Guid ButlerRunId { get; init; }
    public Guid? WorkspaceId { get; init; }
    public Guid? ConversationId { get; init; }
    public string OperationId { get; init; } = string.Empty;
    public string ActionMode { get; init; } = "AskFirst";
    public string Summary { get; init; } = string.Empty;
    public string Status { get; init; } = "queued";
    public bool CancellationRequested { get; init; }
    public string? ResultJson { get; init; }
    public string? Problem { get; init; }
    public string? LastMonitorError { get; init; }
    public long LastEventSequence { get; init; } = -1;
    public IReadOnlyList<ProjectButlerEvent> Events { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Cursor-addressable Butler progress copied into the owning Concierge Project.</summary>
public sealed record ProjectButlerEvent(long Sequence, string Type, string Data, DateTimeOffset CreatedAt);

public enum ProjectAgentStatus
{
    Queued,
    Running,
    Paused,
    Review,
    Failed,
}

/// <summary>Persistent projects, independent of transient chat turns.</summary>
public interface IProjectStore
{
    Task<ConciergeProject> CreateAsync(ConciergeProject project, CancellationToken cancellationToken = default);
    Task<ConciergeProject?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<ProjectAgentEvent>> ReadAgentEventsAsync(Guid projectId, Guid runId,
        int afterSequence = -1, CancellationToken cancellationToken = default)
    {
        var project = await GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        return project?.AgentRuns.SingleOrDefault(run => run.Id == runId)?.Events
            .Where(item => item.Sequence > afterSequence).OrderBy(item => item.Sequence).ToArray() ?? [];
    }
    Task<IReadOnlyList<ConciergeProject>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    /// <summary>Save only if the caller's revision is still current; returns the next revision.</summary>
    Task<ConciergeProject> SaveAsync(ConciergeProject project, CancellationToken cancellationToken = default);
    Task<StartupJourney> CreateStartupAsync(StartupJourney journey, CancellationToken cancellationToken = default);
    Task<StartupJourney?> GetStartupAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StartupJourney>> ListStartupsAsync(CancellationToken cancellationToken = default);
    Task<StartupJourney> SaveStartupAsync(StartupJourney journey, CancellationToken cancellationToken = default);
    /// <summary>Create a project and link it to a Start-Up journey in one transaction.</summary>
    Task<ConciergeProject> CreateProjectForStartupAsync(Guid startupId, ConciergeProject project, CancellationToken cancellationToken = default);
    Task<StartupJourney> LinkProjectToStartupAsync(Guid startupId, Guid projectId, CancellationToken cancellationToken = default);
    Task<StartupJourney> LinkConversationToStartupAsync(Guid startupId, Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when another surface has saved a newer revision of a project.</summary>
public sealed class ProjectRevisionConflictException(Guid projectId)
    : InvalidOperationException($"Project {projectId} changed elsewhere. Reload it before saving again.")
{
    public Guid ProjectId { get; } = projectId;
}

/// <summary>Thrown when another surface has saved a newer Start-Up Mode journey revision.</summary>
public sealed class StartupJourneyRevisionConflictException(Guid journeyId)
    : InvalidOperationException($"Start-Up Mode journey {journeyId} changed elsewhere. Reload it before saving again.")
{
    public Guid JourneyId { get; } = journeyId;
}

internal sealed class ProjectDocumentRow
{
    public Guid Id { get; set; }
    public int Revision { get; set; }
    public string Json { get; set; } = string.Empty;
}

internal sealed class StartupDocumentRow
{
    public Guid Id { get; set; }
    public int Revision { get; set; }
    public string Json { get; set; } = string.Empty;
}

internal sealed class ProjectDbContext(DbContextOptions<ProjectDbContext> options) : DbContext(options)
{
    public DbSet<ProjectDocumentRow> Projects => Set<ProjectDocumentRow>();
    public DbSet<StartupDocumentRow> StartupJourneys => Set<StartupDocumentRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectDocumentRow>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Json).IsRequired();
        });
        modelBuilder.Entity<StartupDocumentRow>(entity =>
        {
            entity.ToTable("StartupJourneys");
            entity.HasKey(journey => journey.Id);
            entity.Property(journey => journey.Json).IsRequired();
        });
    }
}

/// <summary>
/// SQLite project documents live alongside, but separately from, the chat database. This
/// lets existing Concierge databases gain the new table without destructive EnsureCreated
/// behavior; project data remains on the same machine and receives independent revisions.
/// </summary>
internal sealed class SqliteProjectStore(IDbContextFactory<ProjectDbContext> factory) : IProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lazy<Task> _schemaReady = new(() => EnsureSchemaAsync(factory), LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<ConciergeProject> CreateAsync(ConciergeProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);
        if (project.StartupJourneyId is not null)
            throw new ArgumentException("Link a created project to a Start-Up Mode journey through the store.", nameof(project));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var created = project with { Revision = 1, CreatedAt = project.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow };
        db.Projects.Add(new ProjectDocumentRow
        {
            Id = created.Id,
            Revision = created.Revision,
            Json = JsonSerializer.Serialize(created, JsonOptions),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<ConciergeProject?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Projects.AsNoTracking().SingleOrDefaultAsync(project => project.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Deserialize(row);
    }

    public async Task<IReadOnlyList<ConciergeProject>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Projects.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Deserialize)
            .Where(project => includeArchived || project.Status != ProjectStatus.Archived)
            .OrderByDescending(project => project.UpdatedAt)
            .ToList();
    }

    public async Task<ConciergeProject> SaveAsync(ConciergeProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);
        if (project.Revision <= 0)
            throw new ArgumentException("Save a project created by this store so its revision is known.", nameof(project));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var currentRow = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == project.Id, cancellationToken).ConfigureAwait(false);
        if (currentRow is null)
            throw new ProjectRevisionConflictException(project.Id);
        if (Deserialize(currentRow).StartupJourneyId != project.StartupJourneyId)
            throw new InvalidOperationException("Update business links through LinkProjectToStartupAsync.");
        var saved = project with { Revision = project.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        var changed = await db.Projects
            .Where(row => row.Id == project.Id && row.Revision == project.Revision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Revision, saved.Revision)
                .SetProperty(row => row.Json, JsonSerializer.Serialize(saved, JsonOptions)), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
            throw new ProjectRevisionConflictException(project.Id);
        return saved;
    }

    public async Task<StartupJourney> CreateStartupAsync(StartupJourney journey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journey);
        Validate(journey);
        if (journey.ProjectIds.Count != 0)
            throw new ArgumentException("Create a journey first, then link projects through the store.", nameof(journey));
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var created = journey with { Revision = 1, UpdatedAt = DateTimeOffset.UtcNow };
        db.StartupJourneys.Add(new StartupDocumentRow
        {
            Id = created.Id,
            Revision = created.Revision,
            Json = JsonSerializer.Serialize(created, JsonOptions),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<StartupJourney?> GetStartupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.StartupJourneys.AsNoTracking().SingleOrDefaultAsync(journey => journey.Id == id, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Deserialize(row);
    }

    public async Task<IReadOnlyList<StartupJourney>> ListStartupsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.StartupJourneys.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Deserialize).OrderByDescending(journey => journey.UpdatedAt).ToList();
    }

    public async Task<StartupJourney> SaveStartupAsync(StartupJourney journey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journey);
        Validate(journey);
        if (journey.Revision <= 0)
            throw new ArgumentException("Save a Start-Up Mode journey created by this store so its revision is known.", nameof(journey));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var currentRow = await db.StartupJourneys.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == journey.Id, cancellationToken).ConfigureAwait(false);
        if (currentRow is null)
            throw new StartupJourneyRevisionConflictException(journey.Id);
        var current = Deserialize(currentRow);
        if (!current.ProjectIds.ToHashSet().SetEquals(journey.ProjectIds))
            throw new InvalidOperationException("Update project links through LinkProjectToStartupAsync.");
        if (!current.ConversationIds.ToHashSet().SetEquals(journey.ConversationIds))
            throw new InvalidOperationException("Update conversation links through LinkConversationToStartupAsync.");
        var saved = journey with { Revision = journey.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        var changed = await db.StartupJourneys
            .Where(row => row.Id == journey.Id && row.Revision == journey.Revision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Revision, saved.Revision)
                .SetProperty(row => row.Json, JsonSerializer.Serialize(saved, JsonOptions)), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
            throw new StartupJourneyRevisionConflictException(journey.Id);
        return saved;
    }

    public async Task<ConciergeProject> CreateProjectForStartupAsync(
        Guid startupId,
        ConciergeProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);
        if (startupId == Guid.Empty || project.StartupJourneyId is not null || project.Revision != 0)
            throw new ArgumentException("Provide a new, unlinked project and a valid Start-Up journey ID.", nameof(project));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var startupRow = await db.StartupJourneys.SingleOrDefaultAsync(row => row.Id == startupId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Start-Up Mode journey {startupId} was not found.");
        var startup = Deserialize(startupRow);
        if (startup.Status is ProjectStatus.Completed or ProjectStatus.Archived)
            throw new InvalidOperationException("This Start-Up journey is closed and cannot accept another project.");

        var created = project with
        {
            StartupJourneyId = startupId,
            Revision = 1,
            CreatedAt = project.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Validate(created);
        db.Projects.Add(new ProjectDocumentRow
        {
            Id = created.Id,
            Revision = created.Revision,
            Json = JsonSerializer.Serialize(created, JsonOptions),
        });

        var linkedStartup = startup with
        {
            ProjectIds = startup.ProjectIds.Append(created.Id).Distinct().ToArray(),
            Status = startup.Status == ProjectStatus.Planning ? ProjectStatus.Active : startup.Status,
            Revision = startup.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Validate(linkedStartup);
        var changed = await db.StartupJourneys
            .Where(row => row.Id == startup.Id && row.Revision == startup.Revision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Revision, linkedStartup.Revision)
                .SetProperty(row => row.Json, JsonSerializer.Serialize(linkedStartup, JsonOptions)), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
            throw new StartupJourneyRevisionConflictException(startup.Id);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<StartupJourney> LinkProjectToStartupAsync(Guid startupId, Guid projectId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var startupRow = await db.StartupJourneys.SingleOrDefaultAsync(row => row.Id == startupId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Start-Up Mode journey {startupId} was not found.");
        var projectRow = await db.Projects.SingleOrDefaultAsync(row => row.Id == projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} was not found.");
        var startup = Deserialize(startupRow);
        var project = Deserialize(projectRow);
        if (project.StartupJourneyId is { } existingStartup && existingStartup != startupId)
            throw new InvalidOperationException("This project is already linked to another Start-Up Mode journey.");
        if (startup.ProjectIds.Contains(projectId) && project.StartupJourneyId == startupId)
            return startup;

        var linkedProject = project with
        {
            StartupJourneyId = startupId,
            Revision = project.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var linkedStartup = startup with
        {
            ProjectIds = startup.ProjectIds.Append(projectId).Distinct().ToArray(),
            Revision = startup.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Validate(linkedProject);
        Validate(linkedStartup);

        var projectChanged = await db.Projects.Where(row => row.Id == project.Id && row.Revision == project.Revision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Revision, linkedProject.Revision)
                .SetProperty(row => row.Json, JsonSerializer.Serialize(linkedProject, JsonOptions)), cancellationToken)
            .ConfigureAwait(false);
        if (projectChanged == 0)
            throw new ProjectRevisionConflictException(project.Id);
        var startupChanged = await db.StartupJourneys.Where(row => row.Id == startup.Id && row.Revision == startup.Revision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Revision, linkedStartup.Revision)
                .SetProperty(row => row.Json, JsonSerializer.Serialize(linkedStartup, JsonOptions)), cancellationToken)
            .ConfigureAwait(false);
        if (startupChanged == 0)
            throw new StartupJourneyRevisionConflictException(startup.Id);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return linkedStartup;
    }

    public async Task<StartupJourney> LinkConversationToStartupAsync(Guid startupId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty)
            throw new ArgumentException("A conversation must have a stable ID.", nameof(conversationId));

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.StartupJourneys.ToListAsync(cancellationToken).ConfigureAwait(false);
        var startupRow = rows.SingleOrDefault(row => row.Id == startupId)
            ?? throw new KeyNotFoundException($"Start-Up Mode journey {startupId} was not found.");
        var startup = Deserialize(startupRow);
        var linkedElsewhere = rows
            .Where(row => row.Id != startupId)
            .Select(Deserialize)
            .Any(journey => journey.ConversationIds.Contains(conversationId));
        if (linkedElsewhere)
            throw new InvalidOperationException("This conversation is already linked to another Start-Up Mode journey.");
        if (startup.ConversationIds.Contains(conversationId))
            return startup;

        var linked = startup with
        {
            ConversationIds = startup.ConversationIds.Append(conversationId).Distinct().ToArray(),
            Revision = startup.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Validate(linked);
        var changed = await db.StartupJourneys
            .Where(row => row.Id == startup.Id && row.Revision == startup.Revision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Revision, linked.Revision)
                .SetProperty(row => row.Json, JsonSerializer.Serialize(linked, JsonOptions)), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
            throw new StartupJourneyRevisionConflictException(startup.Id);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return linked;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _schemaReady.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaAsync(IDbContextFactory<ProjectDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync().ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
        // EnsureCreated does not add new tables to a database that already has Projects.
        // This additive table creation preserves existing project documents on upgrade.
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS StartupJourneys (Id TEXT NOT NULL CONSTRAINT PK_StartupJourneys PRIMARY KEY, Revision INTEGER NOT NULL, Json TEXT NOT NULL);")
            .ConfigureAwait(false);
    }

    private static ConciergeProject Deserialize(ProjectDocumentRow row)
        => JsonSerializer.Deserialize<ConciergeProject>(row.Json, JsonOptions) is { } project
            ? project with { Revision = row.Revision }
            : throw new InvalidDataException($"Project {row.Id} is empty or unreadable.");

    private static StartupJourney Deserialize(StartupDocumentRow row)
        => JsonSerializer.Deserialize<StartupJourney>(row.Json, JsonOptions) is { } journey
            ? journey with { Revision = row.Revision }
            : throw new InvalidDataException($"Start-Up Mode journey {row.Id} is empty or unreadable.");

    private static void Validate(StartupJourney journey)
    {
        if (journey.Id == Guid.Empty)
            throw new ArgumentException("A Start-Up Mode journey must have a stable ID.", nameof(journey));
        if (!Enum.IsDefined(journey.Type))
            throw new ArgumentOutOfRangeException(nameof(journey), "Choose whether this is a new or existing business.");
        ArgumentException.ThrowIfNullOrWhiteSpace(journey.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(journey.Goal);
        if (journey.Type == BusinessJourneyType.ExistingBusiness && string.IsNullOrWhiteSpace(journey.CurrentContext))
            throw new ArgumentException("An existing business journey needs its current context.", nameof(journey));
        if (journey.ProjectIds.Any(id => id == Guid.Empty) || journey.ProjectIds.Distinct().Count() != journey.ProjectIds.Count)
            throw new ArgumentException("Project links must be unique and non-empty.", nameof(journey));
        if (journey.ConversationIds.Any(id => id == Guid.Empty) || journey.ConversationIds.Distinct().Count() != journey.ConversationIds.Count)
            throw new ArgumentException("Conversation links must be unique and non-empty.", nameof(journey));
    }

    private static void Validate(ConciergeProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.DesiredOutcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.WorkType);
        if (project.Id == Guid.Empty)
            throw new ArgumentException("A project must have a stable ID.", nameof(project));
        if (project.BudgetLimit < 0)
            throw new ArgumentException("A project budget cannot be negative.", nameof(project));
        if (project.ConversationIds.Any(id => id == Guid.Empty)
            || project.ConversationIds.Distinct().Count() != project.ConversationIds.Count)
            throw new ArgumentException("Conversation links must be unique and non-empty.", nameof(project));
        if (project.AgentRuns.Any(run => run.Id == Guid.Empty || string.IsNullOrWhiteSpace(run.Name)
                || string.IsNullOrWhiteSpace(run.Task) || !Enum.IsDefined(run.Status))
            || project.AgentRuns.Select(run => run.Id).Distinct().Count() != project.AgentRuns.Count
            || project.AgentRuns.Any(run => run.Events.Select(item => item.Sequence).Where((sequence, index) => sequence != index).Any()
                || run.Events.Any(item => item.Id == Guid.Empty || !Enum.IsDefined(item.Type))
                || run.Events.Select(item => item.Id).Distinct().Count() != run.Events.Count)
            || project.AgentRuns.Any(run => run.ConversationId is { } conversationId
                && !project.ConversationIds.Contains(conversationId))
            || project.AgentRuns.Any(run => run.WorkspaceId is { } workspaceId
                && (!project.Workspaces.Any(workspace => workspace.Id == workspaceId)
                    || run.ConversationId is { } conversationId
                        && !project.Workspaces.Single(workspace => workspace.Id == workspaceId).ConversationIds.Contains(conversationId))))
            throw new ArgumentException("Agent tasks must have unique IDs, valid work, and belong to their Project or Workspace scope.", nameof(project));
        if (project.AgentRequests.Any(request => request.RunId == Guid.Empty
                || request.Fingerprint.Length != 64
                || !request.Fingerprint.All(Uri.IsHexDigit))
            || project.AgentRequests.Select(request => request.Fingerprint).Distinct(StringComparer.Ordinal).Count() != project.AgentRequests.Count
            || project.AgentRequests.Select(request => request.RunId).Distinct().Count() != project.AgentRequests.Count)
            throw new ArgumentException("Project-agent request identities must have unique fingerprints and run IDs.", nameof(project));
        if (project.ButlerRuns.Any(run => run.Id == Guid.Empty || run.ConciergeRunId == Guid.Empty
                || (run.ButlerRunId == Guid.Empty && (string.IsNullOrWhiteSpace(run.IdempotencyKey)
                    || string.IsNullOrWhiteSpace(run.RequestJson) && !run.CancellationRequested))
                || string.IsNullOrWhiteSpace(run.OperationId)
                || string.IsNullOrWhiteSpace(run.Summary) || string.IsNullOrWhiteSpace(run.Status))
            || project.ButlerRuns.Select(run => run.Id).Distinct().Count() != project.ButlerRuns.Count
            || project.ButlerRuns.Where(run => run.ButlerRunId != Guid.Empty).Select(run => run.ButlerRunId).Distinct().Count()
                != project.ButlerRuns.Count(run => run.ButlerRunId != Guid.Empty)
            || project.ButlerRuns.Select(run => run.IdempotencyKey).Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal).Count() != project.ButlerRuns.Count(run => !string.IsNullOrWhiteSpace(run.IdempotencyKey))
            || project.ButlerRuns.Any(run => run.Events.Select(item => item.Sequence)
                .Where((sequence, index) => sequence != index).Any()
                || run.WorkspaceId is { } workspaceId && !project.Workspaces.Any(workspace => workspace.Id == workspaceId)))
            throw new ArgumentException("Butler runs must have unique identities and ordered event history.", nameof(project));

        var workflowIds = new HashSet<Guid>();
        var stepIds = new HashSet<Guid>();
        foreach (var workflow in project.Workflows)
        {
            if (workflow.Id == Guid.Empty || !workflowIds.Add(workflow.Id))
                throw new ArgumentException("Workflow IDs must be unique and non-empty.", nameof(project));
            ArgumentException.ThrowIfNullOrWhiteSpace(workflow.Name);
            foreach (var step in workflow.Steps)
            {
                if (step.Id == Guid.Empty || !stepIds.Add(step.Id))
                    throw new ArgumentException("Step IDs must be unique and non-empty within a project.", nameof(project));
                ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);
            }
        }

        var workspaceIds = new HashSet<Guid>();
        foreach (var workspace in project.Workspaces)
        {
            if (workspace.Id == Guid.Empty || !workspaceIds.Add(workspace.Id))
                throw new ArgumentException("Workspace IDs must be unique and non-empty.", nameof(project));
            ArgumentException.ThrowIfNullOrWhiteSpace(workspace.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspace.DesiredOutcome);
            if (workspace.WorkflowIds.Any(id => !workflowIds.Contains(id))
                || workspace.WorkflowIds.Distinct().Count() != workspace.WorkflowIds.Count)
                throw new ArgumentException("Workspace workflow links must be unique and belong to this project.", nameof(project));
            if (workspace.ConversationIds.Any(id => id == Guid.Empty)
                || workspace.ConversationIds.Distinct().Count() != workspace.ConversationIds.Count
                || workspace.ConversationIds.Any(id => !project.ConversationIds.Contains(id)))
                throw new ArgumentException("Workspace conversations must be unique, non-empty, and linked to this project.", nameof(project));
        }

        foreach (var step in project.Workflows.SelectMany(workflow => workflow.Steps))
        {
            if (step.DependsOn.Any(dependency => !stepIds.Contains(dependency) || dependency == step.Id))
                throw new ArgumentException("Every dependency must refer to another step in this project.", nameof(project));
        }

        var stepsById = project.Workflows.SelectMany(workflow => workflow.Steps).ToDictionary(step => step.Id);
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        bool HasCycle(Guid stepId)
        {
            if (visited.Contains(stepId)) return false;
            if (!visiting.Add(stepId)) return true;
            foreach (var dependencyId in stepsById[stepId].DependsOn)
            {
                if (HasCycle(dependencyId)) return true;
            }
            visiting.Remove(stepId);
            visited.Add(stepId);
            return false;
        }
        if (stepsById.Keys.Any(HasCycle))
            throw new ArgumentException("Workflow dependencies cannot form a cycle.", nameof(project));

        var artifactIds = project.Artifacts.Select(artifact => artifact.Id).ToList();
        var artifacts = artifactIds.ToHashSet();
        if (artifactIds.Any(id => id == Guid.Empty) || artifactIds.Distinct().Count() != artifactIds.Count
            || project.Workflows.SelectMany(workflow => workflow.Steps).Any(step => step.ArtifactIds.Any(id => !artifacts.Contains(id)))
            || project.Workspaces.Any(workspace => workspace.ArtifactIds.Any(id => !artifacts.Contains(id))))
            throw new ArgumentException("Workflows and Workspaces may reference only artifacts attached to this project.", nameof(project));
    }
}

/// <summary>Registers project persistence in a sibling database next to the chat database.</summary>
public static class ConciergeProjectServiceCollectionExtensions
{
    public static IServiceCollection AddConciergeProjects(this IServiceCollection services, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("The project database needs a parent directory.", nameof(databasePath));
        Directory.CreateDirectory(directory);
        var projectDatabasePath = Path.Combine(directory, "projects.db");
        services.AddDbContextFactory<ProjectDbContext>(options => options.UseSqlite($"Data Source={projectDatabasePath}"));
        services.TryAddSingleton<IProjectStore, SqliteProjectStore>();
        services.TryAddSingleton<IButlerWorkRunApi, ButlerWorkRunApiUnavailable>();
        services.TryAddSingleton<ProjectAgentCoordinator>();
        services.TryAddSingleton<ProjectButlerRunMonitor>();
        services.AddHostedService<ProjectButlerRunMonitor>(provider => provider.GetRequiredService<ProjectButlerRunMonitor>());
        services.TryAddSingleton<IToolApprovalService>(_ => UnavailableToolApprovalService.Instance);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, ProjectWorkToolSource>());
        return services;
    }
}
