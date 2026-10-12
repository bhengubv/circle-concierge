using System.Runtime.CompilerServices;
using Concierge.Shared.Chat;
using Concierge.Shared.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ProjectAgentCoordinatorTests
{
    [Fact]
    public async Task Agent_analysis_runs_on_Circle_AI_and_persists_a_reviewable_result()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Launch an ad business", "Find a first customer", "Business") with
        {
            Workflows = [new ProjectWorkflow
            {
                Name = "Validate",
                DesiredOutcome = "Test demand",
                Status = ProjectWorkStatus.Active,
                Steps = [new ProjectStep { Name = "Interview owners", Status = ProjectWorkStatus.Planned }],
            }],
        });
        var runtime = new ScriptedRuntime("The first segment to validate is local retailers.");
        var coordinator = provider.GetRequiredService<ProjectAgentCoordinator>();

        var queued = await coordinator.QueueAsync(project.Id, runtime, "Market researcher", "Identify the first customer segment.");
        var review = await WaitForStatus(store, project.Id, queued.Id, ProjectAgentStatus.Review);

        Assert.Equal("The first segment to validate is local retailers.", review.Output);
        Assert.Equal("circleai", review.RuntimeId);
        Assert.Equal(queued.Id, runtime.LastRunId);
        Assert.Contains("read-only Project analysis agent", runtime.LastMessages[0].Content);
        Assert.Contains("Never infer missing people, names, counts, specific chores, schedules, or completed work", runtime.LastMessages[0].Content);
        Assert.Contains("recorded facts, unknowns, general guidance, and questions needed next", runtime.LastMessages[0].Content);
        Assert.Contains("Launch an ad business", Assert.Single(runtime.LastMessages, message => message.Role == "user").Content);
        var context = runtime.LastMessages[1].Content;
        Assert.Contains("Validate", context);
        Assert.Contains("\"Status\":\"Active\"", context);
        Assert.Contains("\"Status\":\"Planned\"", context);
        Assert.DoesNotContain("\"Status\":0", context);
        Assert.DoesNotContain("ConversationId", context);
        Assert.DoesNotContain("Revision", context);
    }

    [Fact]
    public async Task Retrying_a_project_agent_dispatch_with_the_same_run_id_replays_without_enqueuing_again()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Website", "Plan the launch"));
        var runtime = new ScriptedRuntime("Review result");
        var coordinator = provider.GetRequiredService<ProjectAgentCoordinator>();
        var runId = Guid.NewGuid();

        var first = await coordinator.QueueAsync(project.Id, runtime, "Launch analyst", "List the launch steps.", requestRunId: runId);
        var completed = await WaitForStatus(store, project.Id, first.Id, ProjectAgentStatus.Review);
        using var restartedProvider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var retry = await restartedProvider.GetRequiredService<ProjectAgentCoordinator>().QueueAsync(
            project.Id, runtime, "Launch analyst", "List the launch steps.", requestRunId: runId);

        Assert.Equal(runId, first.Id);
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(completed.Output, retry.Output);
        Assert.Equal(1, runtime.CallCount);
        Assert.Matches("^[A-F0-9]{64}$", retry.RequestFingerprint);
    }

    [Fact]
    public async Task Reusing_a_project_agent_run_id_for_different_work_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Website", "Plan the launch"));
        var runtime = new ScriptedRuntime("Review result");
        var coordinator = provider.GetRequiredService<ProjectAgentCoordinator>();
        var runId = Guid.NewGuid();

        var first = await coordinator.QueueAsync(
            project.Id, runtime, "Launch analyst", "List the launch steps.", requestRunId: runId);
        await WaitForStatus(store, project.Id, first.Id, ProjectAgentStatus.Review);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.QueueAsync(
            project.Id, runtime, "Launch analyst", "Publish the launch announcement.", requestRunId: runId));
        Assert.Contains("different Project-agent work", error.Message);
        Assert.Equal(1, runtime.CallCount);
    }

    [Fact]
    public async Task Retry_after_service_restart_recovers_the_saved_project_agent_request_id()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Website", "Plan the launch"));
        var runtime = new ScriptedRuntime("Review result");
        var task = "List the launch steps.";
        var first = await provider.GetRequiredService<ProjectAgentCoordinator>().QueueAsync(
            project.Id, runtime, "Launch analyst", task);
        var completed = await WaitForStatus(store, project.Id, first.Id, ProjectAgentStatus.Review);

        using var restartedProvider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var retry = await restartedProvider.GetRequiredService<ProjectAgentCoordinator>().QueueAsync(
            project.Id, runtime, "Launch analyst", task);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(completed.Output, retry.Output);
        Assert.Equal(1, runtime.CallCount);
        Assert.Contains((await restartedProvider.GetRequiredService<IProjectStore>().GetAsync(project.Id))!.AgentRequests,
            request => request.RunId == first.Id && request.Fingerprint == retry.RequestFingerprint);
    }

    [Fact]
    public async Task Explicit_run_again_rotates_the_saved_identity_for_the_same_intent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Website", "Plan the launch"));
        var runtime = new ScriptedRuntime("Review result");
        var coordinator = provider.GetRequiredService<ProjectAgentCoordinator>();
        var first = await coordinator.QueueAsync(project.Id, runtime, "Launch analyst", "List the launch steps.");
        await WaitForStatus(store, project.Id, first.Id, ProjectAgentStatus.Review);

        var second = await coordinator.QueueAsync(project.Id, runtime, "Launch analyst", "List the launch steps.",
            requestRunId: Guid.NewGuid());
        await WaitForStatus(store, project.Id, second.Id, ProjectAgentStatus.Review);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, runtime.CallCount);
        Assert.Single((await store.GetAsync(project.Id))!.AgentRequests,
            request => request.Fingerprint == first.RequestFingerprint && request.RunId == second.Id);
    }

    [Fact]
    public async Task Concurrent_hosts_claim_one_saved_project_agent_run_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var firstProvider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        using var secondProvider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var project = await firstProvider.GetRequiredService<IProjectStore>()
            .CreateAsync(ConciergeProject.Create("Website", "Plan the launch"));
        var runtime = new ScriptedRuntime("Review result");
        var runId = Guid.NewGuid();
        var firstCoordinator = firstProvider.GetRequiredService<ProjectAgentCoordinator>();
        var secondCoordinator = secondProvider.GetRequiredService<ProjectAgentCoordinator>();

        var queued = await Task.WhenAll(
            firstCoordinator.QueueAsync(project.Id, runtime, "Launch analyst", "List the launch steps.", requestRunId: runId),
            secondCoordinator.QueueAsync(project.Id, runtime, "Launch analyst", "List the launch steps.", requestRunId: runId));
        await WaitForStatus(firstProvider.GetRequiredService<IProjectStore>(), project.Id, runId, ProjectAgentStatus.Review);
        var saved = await firstProvider.GetRequiredService<IProjectStore>().GetAsync(project.Id);

        Assert.All(queued, run => Assert.Equal(runId, run.Id));
        Assert.Single(saved!.AgentRuns, run => run.Id == runId);
        Assert.Equal(1, runtime.CallCount);
    }

    [Fact]
    public async Task Project_agent_events_persist_in_sequence_and_resume_after_a_cursor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Website", "Plan the launch"));
        var runtime = new ScriptedRuntime("Persisted review output");
        var run = await provider.GetRequiredService<ProjectAgentCoordinator>().QueueAsync(
            project.Id, runtime, "Launch analyst", "List the launch steps.");
        var review = await WaitForStatus(store, project.Id, run.Id, ProjectAgentStatus.Review);
        var events = await store.ReadAgentEventsAsync(project.Id, run.Id);

        Assert.Equal(new[] { ProjectAgentEventType.Queued, ProjectAgentEventType.Started,
            ProjectAgentEventType.Output, ProjectAgentEventType.ReviewReady }, events.Select(item => item.Type));
        Assert.Equal(Enumerable.Range(0, events.Count), events.Select(item => item.Sequence));
        Assert.Equal(review.Output, string.Concat(events.Where(item => item.Type == ProjectAgentEventType.Output)
            .Select(item => item.Data)));
        Assert.Equal(events.Skip(2), await store.ReadAgentEventsAsync(project.Id, run.Id, afterSequence: 1));

        using var restartedProvider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        Assert.Equal(events.Skip(3), await restartedProvider.GetRequiredService<IProjectStore>()
            .ReadAgentEventsAsync(project.Id, run.Id, afterSequence: 2));
    }

    [Fact]
    public async Task Pausing_an_agent_preserves_its_partial_notes_in_the_Project()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Website", "Plan the launch", "Business"));
        var runtime = new ScriptedRuntime("Partial finding", holdAfterFirstChunk: true);
        var coordinator = provider.GetRequiredService<ProjectAgentCoordinator>();
        var queued = await coordinator.QueueAsync(project.Id, runtime, "Research agent", "Review likely launch risks.");

        await runtime.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.PauseAsync(project.Id, queued.Id);
        var paused = await WaitForStatus(store, project.Id, queued.Id, ProjectAgentStatus.Paused);

        Assert.Equal("Partial finding", paused.Output);
        Assert.Contains("Paused", paused.Problem);
    }

    [Fact]
    public async Task Workspace_agent_receives_only_its_own_workflows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var included = new ProjectWorkflow { Name = "Customer research", DesiredOutcome = "Understand local needs" };
        var excluded = new ProjectWorkflow { Name = "Finance", DesiredOutcome = "Prepare the accounts" };
        var workspace = new ProjectWorkspace
        {
            Name = "Customer discovery",
            DesiredOutcome = "Choose the first customer group",
            WorkflowIds = [included.Id],
        };
        var project = await store.CreateAsync(ConciergeProject.Create("Launch", "Start a local business") with
        {
            Workflows = [included, excluded],
            Workspaces = [workspace],
        });
        var runtime = new ScriptedRuntime("Review result");

        var run = await provider.GetRequiredService<ProjectAgentCoordinator>().QueueAsync(
            project.Id, runtime, "Discovery analyst", "Identify the first customer question.", workspaceId: workspace.Id);
        var review = await WaitForStatus(store, project.Id, run.Id, ProjectAgentStatus.Review);
        var agentPrompt = runtime.LastMessages.Single(message => message.Role == "user").Content;

        Assert.Equal(workspace.Id, review.WorkspaceId);
        Assert.Contains("Customer discovery", agentPrompt);
        Assert.Contains("Customer research", agentPrompt);
        Assert.DoesNotContain("Finance", agentPrompt);
    }

    [Fact]
    public async Task Recovery_marks_an_agent_interrupted_by_process_exit_as_paused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-agents-{Guid.NewGuid():N}", "concierge.db");
        using var provider = new ServiceCollection().AddLogging().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IProjectStore>();
        var project = await store.CreateAsync(ConciergeProject.Create("Music release", "Plan a single release"));
        var agent = new ProjectAgentRun { Name = "Release planner", Task = "List the launch steps.", Status = ProjectAgentStatus.Running };
        await store.SaveAsync(project with { AgentRuns = [agent] });

        await provider.GetRequiredService<ProjectAgentCoordinator>().RecoverInterruptedAsync();

        var recovered = Assert.Single((await store.GetAsync(project.Id))!.AgentRuns);
        Assert.Equal(ProjectAgentStatus.Paused, recovered.Status);
        Assert.Contains("closed", recovered.Problem, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProjectAgentRun> WaitForStatus(IProjectStore store, Guid projectId, Guid runId, ProjectAgentStatus status)
    {
        var stopAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < stopAt)
        {
            var run = (await store.GetAsync(projectId))!.AgentRuns.Single(item => item.Id == runId);
            if (run.Status == status) return run;
            await Task.Delay(20);
        }
        return (await store.GetAsync(projectId))!.AgentRuns.Single(item => item.Id == runId);
    }

    private sealed class ScriptedRuntime(string response, bool holdAfterFirstChunk = false) : IChatRuntime, IRunIdentityChatRuntime
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI test runtime";
        public bool IsReady => true;
        public string StatusMessage => "Ready";
        public IReadOnlyList<ChatTurn> LastMessages { get; private set; } = [];
        public Guid? LastRunId { get; private set; }
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource FirstChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages, Guid runId,
            CancellationToken cancellationToken = default)
        {
            LastRunId = runId;
            return StreamAsync(messages, cancellationToken);
        }

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            LastMessages = messages;
            yield return response;
            FirstChunk.TrySetResult();
            if (holdAfterFirstChunk)
                await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
