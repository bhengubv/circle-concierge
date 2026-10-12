using System.Runtime.CompilerServices;
using System.Reflection;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Context;
using Concierge.Shared.Skills;
using Concierge.Shared.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class TurnRunnerDurabilityTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"turn-run-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private IConversationStore _store = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection().AddConciergeChat(_databasePath).BuildServiceProvider();
        await using var db = await _provider.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>()
            .CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _store = _provider.GetRequiredService<IConversationStore>();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try { File.Delete(_databasePath); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Retrying_a_completed_run_replays_receipt_without_calling_runtime_again()
    {
        var conversation = await _store.StartAsync("local");
        var runtime = new CountingRuntime("The answer");
        var runId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var request = new TurnRequest(conversation.Id, "question", runtime, RunId: runId,
            ProjectId: projectId, ProjectWorkspaceId: workspaceId);

        var first = await Runner(runtime).RunAsync(request);
        var retry = await Runner(runtime).RunAsync(request);

        Assert.Equal("The answer", first.Text);
        Assert.Equal(first.Text, retry.Text);
        Assert.Equal(first.AnsweredBy, retry.AnsweredBy);
        Assert.Equal(first.PlanSteps, retry.PlanSteps);
        Assert.Equal(first.PlanDone, retry.PlanDone);
        Assert.Equal(first.Stopped, retry.Stopped);
        Assert.Equal(1, runtime.Calls);
        var events = await _store.ReadEventsAsync(conversation.Id);
        Assert.Equal(1, events.Count(entry => entry.Type == ConversationEventType.TurnStart));
        Assert.Equal(1, events.Count(entry => entry.Type == ConversationEventType.TurnEnd));
        Assert.Equal(runId, events.Single(entry => entry.Type == ConversationEventType.TurnStart).Id);
        using var start = System.Text.Json.JsonDocument.Parse(
            events.Single(entry => entry.Type == ConversationEventType.TurnStart).Data);
        Assert.Equal(projectId, start.RootElement.GetProperty("ProjectId").GetGuid());
        Assert.Equal(workspaceId, start.RootElement.GetProperty("ProjectWorkspaceId").GetGuid());
    }

    [Fact]
    public async Task Reusing_a_run_id_for_different_input_is_rejected()
    {
        var conversation = await _store.StartAsync("local");
        var runtime = new CountingRuntime("The answer");
        var runId = Guid.NewGuid();
        var runner = Runner(runtime);

        await runner.RunAsync(new TurnRequest(conversation.Id, "first question", runtime, RunId: runId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new TurnRequest(conversation.Id, "different question", runtime, RunId: runId)));

        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task Reusing_a_run_id_in_a_different_project_workspace_is_rejected()
    {
        var conversation = await _store.StartAsync("local");
        var runtime = new CountingRuntime("The answer");
        var runId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var runner = Runner(runtime);

        await runner.RunAsync(new TurnRequest(conversation.Id, "same question", runtime, RunId: runId,
            ProjectId: projectId, ProjectWorkspaceId: Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new TurnRequest(conversation.Id, "same question", runtime, RunId: runId,
                ProjectId: projectId, ProjectWorkspaceId: Guid.NewGuid())));

        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task TurnRunner_exposes_project_tools_only_inside_the_project_scoped_turn()
    {
        var projectTools = new AgentToolRegistry([], _provider.GetServices<IAgentToolSource>());
        var runtime = new CapturingRuntime();
        var projectConversation = await _store.StartAsync("local", "Project work");
        await Runner(runtime, registry: projectTools).RunAsync(new TurnRequest(
            projectConversation.Id, "Review the work", runtime,
            ProjectId: Guid.NewGuid(), ProjectWorkspaceId: Guid.NewGuid()));

        Assert.Contains("project_read_work", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("project_create_workflow", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("project_update_step", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("[requires approval]", runtime.SystemPrompt, StringComparison.Ordinal);

        var chatConversation = await _store.StartAsync("local", "Ordinary chat");
        await Runner(runtime, registry: projectTools).RunAsync(new TurnRequest(
            chatConversation.Id, "A normal question", runtime));

        Assert.DoesNotContain("project_read_work", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("project_create_workflow", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("project_update_step", runtime.SystemPrompt, StringComparison.Ordinal);

        var startupConversation = await _store.StartAsync("local", "Start-Up work");
        await Runner(runtime, registry: projectTools).RunAsync(new TurnRequest(
            startupConversation.Id, "Shape the business plan", runtime,
            StartupJourneyId: Guid.NewGuid()));

        Assert.Contains("startup_read_context", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("startup_create_project", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("project_read_work", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("[requires approval]", runtime.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnRunner_explains_Concierge_and_keeps_Butler_execution_claims_grounded()
    {
        var conversation = await _store.StartAsync("local", "First use");
        var runtime = new CapturingRuntime();

        await Runner(runtime).RunAsync(new TurnRequest(
            conversation.Id, "What can Concierge help me do?", runtime));

        Assert.Contains("assistant inside Concierge", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("powered by Circle AI", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Butler performs the dispatched operational work", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not imply Butler is connected", runtime.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Never say a task was executed", runtime.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_during_stream_records_cancelled_receipt_and_keeps_partial_reply()
    {
        var conversation = await _store.StartAsync("local");
        var runtime = new PartialThenBlockingRuntime();
        var runId = Guid.NewGuid();
        var request = new TurnRequest(conversation.Id, "question", runtime, RunId: runId);
        using var cancellation = new CancellationTokenSource();
        var runner = Runner(runtime);
        var run = runner.RunAsync(request, cancellationToken: cancellation.Token);

        await runtime.WaitingForCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var events = await _store.ReadEventsAsync(conversation.Id);
        var receiptEvent = Assert.Single(events, entry => entry.Type == ConversationEventType.TurnEnd);
        var receipt = System.Text.Json.JsonSerializer.Deserialize<TurnRunReceipt>(receiptEvent.Data)!;
        Assert.Equal("cancelled", receipt.Status);
        Assert.Null(receipt.Text);
        Assert.Contains(events, entry => entry.Type == ConversationEventType.AssistantMessage
            && entry.Data == "Partial answer\n[stream cancelled before completion]");

        var retry = await runner.RunAsync(request);
        Assert.Contains("run was cancelled", retry.Stopped, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task Stop_from_background_runs_cancels_runtime_and_records_cancelled_receipt()
    {
        var conversation = await _store.StartAsync("local");
        var runtime = new PartialThenBlockingRuntime();
        var runId = Guid.NewGuid();
        var runs = new BackgroundRuns();
        var runner = Runner(runtime, runs: runs);
        var run = runner.RunAsync(new TurnRequest(conversation.Id, "question", runtime, RunId: runId));

        await runtime.WaitingForCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(runs.IsRunning(conversation.Id));
        runs.Stop(conversation.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(runs.IsRunning(conversation.Id));
        var events = await _store.ReadEventsAsync(conversation.Id);
        var receiptEvent = Assert.Single(events, entry => entry.Type == ConversationEventType.TurnEnd);
        var receipt = System.Text.Json.JsonSerializer.Deserialize<TurnRunReceipt>(receiptEvent.Data)!;
        Assert.Equal(runId, receipt.RunId);
        Assert.Equal("cancelled", receipt.Status);
        Assert.Contains(events, entry => entry.Type == ConversationEventType.AssistantMessage
            && entry.Data == "Partial answer\n[stream cancelled before completion]");
    }

    [Fact]
    public async Task Concurrent_submissions_with_one_run_id_open_only_one_turn()
    {
        var conversation = await _store.StartAsync("local");
        var runtime = new CountingRuntime("The answer");
        var runId = Guid.NewGuid();
        var request = new TurnRequest(conversation.Id, "question", runtime, RunId: runId);

        var outcomes = await Task.WhenAll(Runner(runtime).RunAsync(request), Runner(runtime).RunAsync(request));

        Assert.Equal(1, runtime.Calls);
        Assert.Contains(outcomes, outcome => outcome.Text == "The answer");
        var events = await _store.ReadEventsAsync(conversation.Id);
        Assert.Single(events, entry => entry.Type == ConversationEventType.TurnStart);
        Assert.Single(events, entry => entry.Type == ConversationEventType.TurnEnd);
    }

    [Fact]
    public async Task Plan_snapshots_are_durable_and_restored_from_the_run_receipt()
    {
        var conversation = await _store.StartAsync("local");
        var answer = "I will start with a plan.\n```plan\n[\"Inspect the project\", \"Summarize findings\"]\n```";
        var runtime = new CountingRuntime(answer);
        var request = new TurnRequest(conversation.Id, "review this project", runtime, RunId: Guid.NewGuid());

        var first = await Runner(runtime).RunAsync(request);
        var retry = await Runner(runtime).RunAsync(request);
        var planEvent = Assert.Single(await _store.ReadEventsAsync(conversation.Id),
            entry => entry.Type == ConversationEventType.TodoWrite);

        Assert.Equal(["Inspect the project", "Summarize findings"], first.PlanSteps);
        Assert.Equal(first.PlanSteps, retry.PlanSteps);
        Assert.Contains("Inspect the project", planEvent.Data, StringComparison.Ordinal);
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task Tool_created_artifact_is_recorded_against_its_tool_result_and_run()
    {
        var conversation = await _store.StartAsync("local");
        var tool = new ArtifactTool();
        var runtime = new SequenceRuntime(
            "```tool-call\n{\"name\":\"create_asset\",\"arguments\":{}}\n```",
            "The image is ready.");
        var runId = Guid.NewGuid();

        await Runner(runtime, registry: new AgentToolRegistry([tool])).RunAsync(
            new TurnRequest(conversation.Id, "Create a campaign image", runtime, RunId: runId));

        var events = await _store.ReadEventsAsync(conversation.Id);
        var artifactEvent = Assert.Single(events, entry => entry.Type == ConversationEventType.ArtifactCreated);
        var artifact = System.Text.Json.JsonSerializer.Deserialize<ConversationArtifactRecord>(artifactEvent.Data)!;
        Assert.Equal("campaign-image.png", artifact.Name);
        Assert.Equal("image/png", artifact.MediaType);
        Assert.Equal(runId, artifact.RunId);
        var sourceEvent = Assert.Single(events, entry => entry.Id == artifact.SourceEventId);
        Assert.Equal(ConversationEventType.ToolResult, sourceEvent.Type);
        Assert.Contains("campaign-image.png", sourceEvent.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Artifacts\"", sourceEvent.Data, StringComparison.Ordinal);
        var projected = await _store.DeriveMessagesAsync(conversation.Id);
        Assert.DoesNotContain(projected, turn => turn.Content == artifactEvent.Data);
    }

    [Fact]
    public async Task Concurrent_turns_on_one_runner_keep_their_plan_state_separate()
    {
        var firstConversation = await _store.StartAsync("local", "First");
        var secondConversation = await _store.StartAsync("local", "Second");
        var (gatedStore, gate) = PlanWriteGateStore.Create(_store, firstConversation.Id);
        var firstPlan = "```plan\n[\"First conversation plan\"]\n```";
        var secondPlan = "```plan\n[\"Second conversation plan\"]\n```";
        var runner = Runner(new CountingRuntime(string.Empty), gatedStore);

        var firstTurn = runner.RunAsync(new TurnRequest(
            firstConversation.Id, "first", new CountingRuntime(firstPlan), RunId: Guid.NewGuid()));
        TurnResult secondTurn;
        try
        {
            await gate.FirstPlanWrite.WaitAsync(TimeSpan.FromSeconds(10));
            secondTurn = await runner.RunAsync(new TurnRequest(
                secondConversation.Id, "second", new CountingRuntime(secondPlan), RunId: Guid.NewGuid()));
        }
        finally
        {
            gate.ReleaseFirstPlanWrite();
        }
        var firstResult = await firstTurn;

        Assert.Equal(["First conversation plan"], firstResult.PlanSteps);
        Assert.Equal(["Second conversation plan"], secondTurn.PlanSteps);

        var firstReceipt = Assert.Single(await _store.ReadEventsAsync(firstConversation.Id),
            entry => entry.Type == ConversationEventType.TurnEnd);
        var secondReceipt = Assert.Single(await _store.ReadEventsAsync(secondConversation.Id),
            entry => entry.Type == ConversationEventType.TurnEnd);
        Assert.Equal(["First conversation plan"], System.Text.Json.JsonSerializer
            .Deserialize<TurnRunReceipt>(firstReceipt.Data)!.PlanSteps);
        Assert.Equal(["Second conversation plan"], System.Text.Json.JsonSerializer
            .Deserialize<TurnRunReceipt>(secondReceipt.Data)!.PlanSteps);
    }

    [Fact]
    public async Task Distinct_turns_for_one_conversation_are_recorded_in_execution_order()
    {
        var conversation = await _store.StartAsync("local", "Ordered turns");
        var gate = new ConversationRunGate();
        var firstRuntime = new BlockingRuntime("First answer");
        var secondRuntime = new CountingRuntime("Second answer");
        var firstRunner = Runner(firstRuntime, runGate: gate);
        var secondRunner = Runner(secondRuntime, runGate: gate);

        var firstTurn = firstRunner.RunAsync(new TurnRequest(
            conversation.Id, "First question", firstRuntime, RunId: Guid.NewGuid()));
        await firstRuntime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTurn = secondRunner.RunAsync(new TurnRequest(
            conversation.Id, "Second question", secondRuntime, RunId: Guid.NewGuid()));

        try
        {
            await Task.Delay(100);
            Assert.Equal(0, secondRuntime.Calls);
        }
        finally
        {
            firstRuntime.Release();
        }

        await Task.WhenAll(firstTurn, secondTurn);
        var exchange = (await _store.ReadEventsAsync(conversation.Id))
            .Where(entry => entry.Type is ConversationEventType.UserMessage or ConversationEventType.AssistantMessage)
            .Select(entry => (entry.Type, entry.Data))
            .ToArray();

        Assert.Equal(
            [
                (ConversationEventType.UserMessage, "First question"),
                (ConversationEventType.AssistantMessage, "First answer"),
                (ConversationEventType.UserMessage, "Second question"),
                (ConversationEventType.AssistantMessage, "Second answer"),
            ],
            exchange);
    }

    private TurnRunner Runner(
        IChatRuntime runtime,
        IConversationStore? store = null,
        ConversationRunGate? runGate = null,
        IAgentToolRegistry? registry = null,
        BackgroundRuns? runs = null)
    {
        registry ??= new AgentToolRegistry([]);
        return new TurnRunner(
            store ?? _store,
            registry,
            new ToolCallScheduler(registry),
            new BasicCompactionEngine(new HeuristicTokenMeter(), runtime),
            new HeadTailToolResultPruner(),
            new RepeatToolReminder(),
            new EmptySkills(),
            new ConciergeToolLoopOptions(),
            runs ?? new BackgroundRuns(),
            new CapturedImages(),
            runGate: runGate);
    }

    private sealed class EmptySkills : ISkillRuntime
    {
        public SkillActivation? Activate(string skillId) => null;
        public string ComposeSystemPrompt(IEnumerable<string> skillIds, int budgetTokens = SkillRuntime.DefaultBudgetTokens) => string.Empty;
    }

    private sealed class CountingRuntime(string answer) : IChatRuntime
    {
        public string Id => "counting";
        public string EngineLabel => "Counting runtime";
        public bool IsReady => true;
        public string StatusMessage => "ready";
        public int Calls { get; private set; }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            await Task.Yield();
            yield return answer;
        }
    }

    private sealed class CapturingRuntime : IChatRuntime
    {
        public string Id => "capturing";
        public string EngineLabel => "Capturing runtime";
        public bool IsReady => true;
        public string StatusMessage => "ready";
        public string SystemPrompt { get; private set; } = string.Empty;

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SystemPrompt = messages.FirstOrDefault(message => message.Role == "system")?.Content ?? string.Empty;
            await Task.Yield();
            yield return "Reviewed.";
        }
    }

    private sealed class SequenceRuntime(params string[] answers) : IChatRuntime
    {
        private int _index;
        public string Id => "sequence";
        public string EngineLabel => "Sequence runtime";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return answers[Math.Min(Interlocked.Increment(ref _index) - 1, answers.Length - 1)];
        }
    }

    private sealed class ArtifactTool : IAgentTool
    {
        public string Name => "create_asset";
        public string Description => "Create a test artifact.";
        public System.Text.Json.Nodes.JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => false;

        public Task<AgentToolResult> InvokeAsync(
            System.Text.Json.Nodes.JsonNode? arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentToolResult(true, "Saved campaign-image.png")
            {
                Artifacts = [new AgentToolArtifact("campaign-image.png", "image/png")],
            });
    }

    private sealed class BlockingRuntime(string answer) : IChatRuntime
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "blocking";
        public string EngineLabel => "Blocking runtime";
        public bool IsReady => true;
        public string StatusMessage => "ready";
        public int Calls { get; private set; }
        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            yield return answer;
        }
    }

    private sealed class PartialThenBlockingRuntime : IChatRuntime
    {
        public TaskCompletionSource WaitingForCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public string Id => "partial-blocking";
        public string EngineLabel => "Partial blocking runtime";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            yield return "Partial answer";
            WaitingForCancellation.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>Pauses the first conversation's plan event to force overlapping turn state.</summary>
    public class PlanWriteGateStore : DispatchProxy
    {
        private IConversationStore _inner = null!;
        private Guid _gatedConversation;
        private readonly TaskCompletionSource _firstPlanWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstPlanWrite => _firstPlanWrite.Task;

        public static (IConversationStore Store, PlanWriteGateStore Gate) Create(IConversationStore inner, Guid gatedConversation)
        {
            var store = DispatchProxy.Create<IConversationStore, PlanWriteGateStore>();
            var gate = (PlanWriteGateStore)(object)store;
            gate._inner = inner;
            gate._gatedConversation = gatedConversation;
            return (store, gate);
        }

        public void ReleaseFirstPlanWrite() => _release.TrySetResult();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            if (targetMethod.Name == nameof(IConversationStore.AppendEventAsync)
                && args[0] is Guid conversationId && conversationId == _gatedConversation
                && args[1] is ConversationEventType.TodoWrite)
            {
                _firstPlanWrite.TrySetResult();
                return AppendAfterReleaseAsync(targetMethod, args);
            }

            return targetMethod.Invoke(_inner, args);
        }

        private async Task<ConversationEvent> AppendAfterReleaseAsync(MethodInfo method, object?[] args)
        {
            await _release.Task.ConfigureAwait(false);
            var append = (Task<ConversationEvent>)method.Invoke(_inner, args)!;
            return await append.ConfigureAwait(false);
        }
    }
}
