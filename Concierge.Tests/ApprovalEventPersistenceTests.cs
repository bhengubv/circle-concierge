using System.Text.Json;
using System.Runtime.CompilerServices;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Context;
using Concierge.Shared.Media;
using Concierge.Shared.Skills;
using Concierge.Shared.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ApprovalEventPersistenceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"approval-events-{Guid.NewGuid():N}.db");
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
    public async Task Person_facing_approval_records_asked_and_decided_events_with_run_identity()
    {
        var conversation = await _store.StartAsync("local");
        var runId = Guid.NewGuid();
        var inner = new InteractiveToolApprovalService(TimeSpan.FromSeconds(5));
        var audit = new ToolApprovalAuditLog();
        var service = new AuditingToolApprovalService(inner, audit, _store);
        using var context = ToolApprovalRunContext.Enter(conversation.Id, runId);

        var request = new ToolApprovalRequest("write_file", "Replace the launch notes", ConciergeToolRisk.High,
            "Show the proposed changes.");
        var pendingDecision = service.RequestAsync(request).AsTask();
        await WaitUntilAsync(() => inner.Pending.Count == 1);

        var pending = Assert.Single(inner.Pending);
        var asked = Assert.Single(await _store.ReadEventsAsync(conversation.Id),
            entry => entry.Type == ConversationEventType.ApprovalAsked);
        var askedPayload = JsonSerializer.Deserialize<ConversationApprovalEvent>(asked.Data)!;
        Assert.Equal(pending.Id, askedPayload.ApprovalId);
        Assert.Equal(runId, askedPayload.RunId);
        Assert.Equal(request.ToolName, askedPayload.ToolName);
        Assert.Null(askedPayload.Decision);

        inner.Answer(pending.Id, ToolApprovalDecision.Allowed);
        Assert.Equal(ToolApprovalDecision.Allowed, await pendingDecision);

        var events = await _store.ReadEventsAsync(conversation.Id);
        var decided = Assert.Single(events, entry => entry.Type == ConversationEventType.ApprovalDecided);
        var decidedPayload = JsonSerializer.Deserialize<ConversationApprovalEvent>(decided.Data)!;
        Assert.Equal(askedPayload.ApprovalId, decidedPayload.ApprovalId);
        Assert.Equal(runId, decidedPayload.RunId);
        Assert.Equal(ToolApprovalDecision.Allowed, decidedPayload.Decision);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal(conversation.Id, auditEntry.ConversationId);
        Assert.Equal(runId, auditEntry.RunId);
        Assert.Equal(pending.Id, auditEntry.ApprovalId);
    }

    [Fact]
    public async Task Act_freely_is_audited_without_fabricating_a_person_prompt()
    {
        var conversation = await _store.StartAsync("local");
        var runId = Guid.NewGuid();
        var inner = new InteractiveToolApprovalService();
        var audit = new ToolApprovalAuditLog();
        var service = new AuditingToolApprovalService(inner, audit, _store);
        using var context = ToolApprovalRunContext.Enter(conversation.Id, runId);
        using var mode = inner.ForTurn(allowWithoutAsking: true);

        var decision = await service.RequestAsync(new ToolApprovalRequest(
            "write_file", "Write the requested file", ConciergeToolRisk.High));

        Assert.Equal(ToolApprovalDecision.Allowed, decision);
        Assert.Empty(await _store.ReadEventsAsync(conversation.Id));
        Assert.Equal(runId, Assert.Single(audit.Entries).RunId);
        Assert.Null(Assert.Single(audit.Entries).ApprovalId);
    }

    [Fact]
    public async Task TurnRunner_attaches_its_identity_to_the_approval_events_from_a_tool()
    {
        var conversation = await _store.StartAsync("local");
        var runId = Guid.NewGuid();
        using var interactive = new InteractiveToolApprovalService(TimeSpan.FromSeconds(5));
        var audit = new ToolApprovalAuditLog();
        var audited = new AuditingToolApprovalService(interactive, audit, _store);
        var registry = new AgentToolRegistry([new ApprovalTool(audited)]);
        var runtime = new ApprovalRuntime();
        var runner = new TurnRunner(
            _store,
            registry,
            new ToolCallScheduler(registry),
            new BasicCompactionEngine(new HeuristicTokenMeter(), runtime),
            new HeadTailToolResultPruner(),
            new RepeatToolReminder(),
            new EmptySkills(),
            new ConciergeToolLoopOptions(),
            new BackgroundRuns(),
            new CapturedImages(),
            runGate: new ConversationRunGate());

        var run = runner.RunAsync(new TurnRequest(
            conversation.Id, "make the requested change", runtime, RunId: runId));
        await WaitUntilAsync(() => interactive.Pending.Count == 1);
        var pending = Assert.Single(interactive.Pending);
        var asked = Assert.Single(await _store.ReadEventsAsync(conversation.Id),
            entry => entry.Type == ConversationEventType.ApprovalAsked);
        var askedPayload = JsonSerializer.Deserialize<ConversationApprovalEvent>(asked.Data)!;

        Assert.Equal(runId, askedPayload.RunId);
        Assert.Equal(pending.Id, askedPayload.ApprovalId);
        interactive.Answer(pending.Id, ToolApprovalDecision.Allowed);

        var result = await run;
        var events = await _store.ReadEventsAsync(conversation.Id);
        var decided = Assert.Single(events, entry => entry.Type == ConversationEventType.ApprovalDecided);
        var decidedPayload = JsonSerializer.Deserialize<ConversationApprovalEvent>(decided.Data)!;
        Assert.Equal(runId, decidedPayload.RunId);
        Assert.Equal(ToolApprovalDecision.Allowed, decidedPayload.Decision);
        Assert.Equal("The approved action is complete.", result.Text);
        Assert.Equal(runId, Assert.Single(audit.Entries).RunId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var limit = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= limit)
                throw new TimeoutException("The approval request did not enter its waiting state.");
            await Task.Delay(10);
        }
    }

    private sealed class EmptySkills : ISkillRuntime
    {
        public SkillActivation? Activate(string skillId) => null;
        public string ComposeSystemPrompt(IEnumerable<string> skillIds, int budgetTokens = SkillRuntime.DefaultBudgetTokens)
            => string.Empty;
    }

    private sealed class ApprovalTool(IToolApprovalService approval) : IAgentTool
    {
        public string Name => "approval_tool";
        public string Description => "A test action that requires permission.";
        public System.Text.Json.Nodes.JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            System.Text.Json.Nodes.JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var decision = await approval.RequestAsync(new ToolApprovalRequest(
                Name, "apply the change", ConciergeToolRisk.High, "reviewable detail"), cancellationToken);
            return decision == ToolApprovalDecision.Allowed
                ? new AgentToolResult(true, "applied")
                : new AgentToolResult(false, "not applied", $"Approval was {decision}.");
        }
    }

    private sealed class ApprovalRuntime : IChatRuntime
    {
        private int _calls;
        public string Id => "approval-test";
        public string EngineLabel => "Approval test runtime";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return Interlocked.Increment(ref _calls) == 1
                ? "```tool-call\n{\"name\":\"approval_tool\",\"arguments\":{}}\n```"
                : "The approved action is complete.";
        }
    }
}
