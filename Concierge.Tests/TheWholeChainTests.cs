using System.Runtime.CompilerServices;
using System.Text.Json;
using Concierge.Away;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Context;
using Concierge.Shared.Design;
using Concierge.Shared.Planning;
using Concierge.Shared.Skills;
using Concierge.Shared.Tools;
using Concierge.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Concierge.Tests;

/// <summary>
/// The whole chain, in one run.
///
/// **Every piece of this was proven separately and the chain never was**, which is exactly
/// the shape this repository keeps finding: parts that each work, joined by something nobody
/// exercised. The wire tests drive the client against a stub listener. The outbox tests drive
/// the queue against a stub listener. The desk tests build `AwayDesk` directly. The endpoint
/// was proven by hand with curl, once, on a machine with a model on it.
///
/// So this is the real client, over real HTTP, to the real endpoints, into the real desk,
/// against a real design file — the only stub is the network being switched off, which is
/// the thing being tested.
/// </summary>
/// <remarks>
/// It stops short of the two ends that need hardware: a microphone on a wrist, and a model
/// that can answer. Both are named where they are missing rather than faked, because a test
/// that pretends to have them would report a working product on a machine that has neither.
/// What is covered is everything in between, which is all of the code.
/// </remarks>
public sealed class TheWholeChainTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"chain-{Guid.NewGuid():N}");

    private WebApplication? _desk;
    private IChatRuntime? _deskRuntime;
    private string _where = string.Empty;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _port = FreePort();
        _where = $"http://127.0.0.1:{_port}";

        await OnAsync();
    }

    /// <summary>
    /// A free port, held only long enough to learn its number.
    ///
    /// The desk is built fresh every time it comes back rather than restarted, because a
    /// stopped host cannot be started again — its own token is already cancelled. Which is
    /// also closer to the thing being simulated: the machine at home was off, and now it is
    /// on, and it is not the same process.
    /// </summary>
    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Concierge is running at home.</summary>
    private async Task OnAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(_where);

        // The real desk over a real design file. Nothing about the away path is stubbed —
        // the only things left out are the model and the approver, and both are optional by
        // construction, which is the point of them being optional.
        builder.Services.AddConciergeChat(Path.Combine(_root, "chat.db"));
        builder.Services.AddSingleton<IDesignStore>(
            _ => new FileDesignStore(Path.Combine(_root, "design.json")));
        var runtime = _deskRuntime ?? new CatalogRuntime();
        builder.Services.AddSingleton<Concierge.Shared.Away.IAway>(provider =>
            new Concierge.Shared.Away.AwayDesk(
                provider.GetRequiredService<IDesignStore>(),
                turn: () => _deskRuntime is null
                    ? null
                    : Runner(provider.GetRequiredService<IConversationStore>(), runtime),
                conversations: provider.GetRequiredService<IConversationStore>(),
                runtimes: [runtime],
                requestLedger: new Concierge.Shared.Away.AwayRequestLedger(Path.Combine(_root, "away-ledger"))));

        _desk = builder.Build();
        _desk.MapConciergeAway();

        await _desk.StartAsync();
    }

    /// <summary>The lift doors close.</summary>
    private async Task OffAsync()
    {
        if (_desk is null)
        {
            return;
        }

        await _desk.StopAsync();
        await _desk.DisposeAsync();
        _desk = null;
    }

    public async Task DisposeAsync()
    {
        await OffAsync();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder left behind is nobody's problem.
        }
    }

    /// <summary>A watch, told where the desk is, with an outbox of its own.</summary>
    private AwayClient AWatch()
    {
        var watch = new AwayClient(new AwayOutbox(Path.Combine(_root, "outbox")));
        watch.PointAt(_where);
        return watch;
    }

    private string Design => File.Exists(Path.Combine(_root, "design.json"))
        ? File.ReadAllText(Path.Combine(_root, "design.json"))
        : string.Empty;

    [Fact]
    public async Task Connected_device_reads_the_desktop_circle_ai_catalogue()
    {
        var capabilities = await AWatch().GetCapabilitiesAsync();

        Assert.NotNull(capabilities);
        Assert.False(capabilities!.CanAnswer);
        Assert.Equal("circleai", capabilities.CircleAi?.RuntimeId);
        Assert.Equal(12.5, capabilities.CircleAi?.Resources?.UsableMemoryGb);
        Assert.Equal("connected", capabilities.CircleAi?.Resources?.Connectivity);
    }

    [Fact]
    public async Task Connected_device_resumes_events_after_desktop_process_restart_from_its_cursor()
    {
        var store = _desk!.Services.GetRequiredService<IConversationStore>();
        var conversation = await store.StartAsync("local", "Resume proof");
        await store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "first event");
        await store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "second event");

        // The phone has already applied sequence zero. The Desktop process then goes away.
        await OffAsync();
        await OnAsync();

        var resumed = await AWatch().ReadConversationEventsAsync(conversation.Id, afterSeq: 0);

        Assert.Collection(resumed,
            entry =>
            {
                Assert.Equal(1, entry.Seq);
                Assert.Equal(ConversationEventType.AssistantMessage, entry.Type);
                Assert.Equal("second event", entry.Data);
        });
    }

    [Fact]
    public async Task Connected_device_catches_up_long_event_logs_in_bounded_cursor_pages()
    {
        var store = _desk!.Services.GetRequiredService<IConversationStore>();
        var conversation = await store.StartAsync("local", "Paged sync proof");
        for (var index = 0; index < 5; index++)
            await store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, $"event-{index}");

        var watch = AWatch();
        var first = await watch.ReadConversationEventsPageAsync(conversation.Id, afterSeq: -1, pageSize: 2);
        var second = await watch.ReadConversationEventsPageAsync(conversation.Id, afterSeq: first[^1].Seq, pageSize: 2);
        var third = await watch.ReadConversationEventsPageAsync(conversation.Id, afterSeq: second[^1].Seq, pageSize: 2);

        Assert.Equal([0, 1], first.Select(entry => entry.Seq));
        Assert.Equal([2, 3], second.Select(entry => entry.Seq));
        Assert.Collection(third, entry => Assert.Equal(4, entry.Seq));
    }

    [Fact]
    public async Task Mobile_local_answer_syncs_to_Desktop_by_event_identity_without_running_a_second_turn()
    {
        var conversationId = Guid.NewGuid();
        var events = new[]
        {
            new ConversationEvent
            {
                Id = Guid.NewGuid(), ConversationId = conversationId, Seq = 0,
                Type = ConversationEventType.UserMessage, Data = "Reply with MOBILE_TEST_OK.",
                CreatedAt = At("07:10"),
            },
            new ConversationEvent
            {
                Id = Guid.NewGuid(), ConversationId = conversationId, Seq = 1,
                Type = ConversationEventType.AssistantMessage, Data = "now", ProducedBy = "Circle AI on phone",
                CreatedAt = At("07:10").AddSeconds(1),
            },
        };

        var phone = AWatch();
        Assert.True(await phone.SynchronizeConversationEventsAsync(conversationId, events));

        var store = _desk!.Services.GetRequiredService<IConversationStore>();
        var desktopEvents = await store.ReadEventsAsync(conversationId);
        Assert.Equal(events.Select(entry => entry.Id), desktopEvents.Select(entry => entry.Id));
        Assert.Equal(["Reply with MOBILE_TEST_OK.", "now"], desktopEvents.Select(entry => entry.Data));

        // Reconnect/restart replay is safe and does not invoke the Desktop runtime.
        Assert.True(await phone.SynchronizeConversationEventsAsync(conversationId, events));
        Assert.Equal(2, (await store.ReadEventsAsync(conversationId)).Count);
    }

    [Fact]
    public async Task Reply_returns_only_the_new_event_tail_after_the_submitted_snapshot()
    {
        var store = _desk!.Services.GetRequiredService<IConversationStore>();
        var conversation = await store.StartAsync("local", "Delta response proof");
        for (var index = 0; index < 12; index++)
            await store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, $"older-{index}");
        const string currentPrompt = "Continue this conversation";
        await store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, currentPrompt);
        var snapshot = await store.ReadEventsAsync(conversation.Id);
        var cursor = snapshot[^1].Seq;

        var reply = await AWatch().SayInConversationAsync(
            currentPrompt, new Situation(At("06:22")), conversation.Id, snapshot);

        var eventDelta = Assert.Single(reply.Events!);
        Assert.Equal(cursor + 1, eventDelta.Seq);
        Assert.Equal(ConversationEventType.AssistantMessage, eventDelta.Type);
        Assert.False(string.IsNullOrWhiteSpace(eventDelta.Data));
    }

    /// <summary>
    /// **The morning this product is for.**
    ///
    /// Out of range, say two things, they are held. Signal comes back, they land in the order
    /// they were said. Glance at the wrist, see what changed. Say no. It goes back, and there
    /// is nothing left to take back.
    ///
    /// Every step is the real code. The only thing pretended is the lift.
    /// </summary>
    [Fact]
    public async Task Said_out_of_range_lands_later_and_can_be_taken_back()
    {
        var watch = AWatch();

        // ── In the lift ───────────────────────────────────────────────────
        await OffAsync();

        var kept = await watch.SayAsync(
            "add a heading that says Sports Day", new Situation(At("06:14")));

        Assert.False(kept.Understood, "it cannot have landed with nothing listening");

        await watch.SayAsync("make it night", new Situation(At("06:15")));

        Assert.Equal(2, watch.Waiting);
        Assert.DoesNotContain("Sports Day", Design, StringComparison.Ordinal);

        // ── Out of the lift ───────────────────────────────────────────────
        await OnAsync();

        Assert.Equal(2, await watch.FlushAsync());
        Assert.Equal(0, watch.Waiting);

        // Both landed, and the first one is in the file the desk actually keeps.
        Assert.Contains("Sports Day", Design, StringComparison.Ordinal);

        // ── The glance ────────────────────────────────────────────────────
        var changed = await watch.LastChangeAsync();

        Assert.NotNull(changed);
        Assert.False(string.IsNullOrWhiteSpace(changed!.What));
        Assert.True(changed.CanUndo, "there is nothing to say no to");

        // ── Saying no ─────────────────────────────────────────────────────
        var back = await watch.UndoAsync();

        Assert.NotNull(back);
        Assert.False(back!.CanUndo, "one step back is all there is with no canvas open");

        // And it is a design change, not a message: the look went back with it.
        Assert.DoesNotContain("\"Night\"", Design, StringComparison.Ordinal);

        // Nothing left to take back, said as nothing rather than as a failure.
        Assert.Null(await watch.UndoAsync());
    }

    /// <summary>
    /// A picture crosses whole, and is refused honestly when it is not one.
    ///
    /// The eye cannot be proven further than this on a machine with no vision model — what is
    /// checked is that the bytes arrive, are judged by what they are rather than what they are
    /// called, and that nothing pretends to have looked.
    /// </summary>
    [Fact]
    public async Task A_picture_crosses_and_bytes_that_are_not_one_are_refused()
    {
        var watch = AWatch();

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var looked = await watch.LookAsync(
            "what is this?", new Situation(At("06:20")), new Looked("wall.png", png));

        // With no model at all there is nothing to answer, and that is said rather than the
        // picture quietly becoming a design change.
        Assert.False(looked.Understood);
        Assert.False(string.IsNullOrWhiteSpace(looked.Reply));

        var rubbish = await watch.LookAsync(
            "what is this?",
            new Situation(At("06:21")),
            new Looked("wall.png", System.Text.Encoding.UTF8.GetBytes("not a picture at all")));

        Assert.False(rubbish.Understood);
        Assert.Contains("not a picture", rubbish.Reply!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mobile_project_agent_runs_on_Desktop_with_the_same_durable_run_id_and_returns_full_notes()
    {
        var desktopModel = new AnswerRuntime("Recorded facts:\nThe launch has two milestones.\n\nUnknowns:\nThe target date is not set.");
        await OffAsync();
        _deskRuntime = desktopModel;
        await OnAsync();

        await using var mobile = new ServiceCollection()
            .AddConciergeChat(Path.Combine(_root, "mobile.db"))
            .BuildServiceProvider();
        var projects = mobile.GetRequiredService<IProjectStore>();
        var project = await projects.CreateAsync(ConciergeProject.Create("Launch", "Release the first version"));
        var coordinator = new ProjectAgentCoordinator(projects);
        var queued = await coordinator.QueueAsync(project.Id, new AwayProjectAgentRuntime(AWatch()),
            "Launch analyst", "Review the milestones and identify unknowns.");

        var completed = await WaitForAgentStatusAsync(projects, project.Id, queued.Id, ProjectAgentStatus.Review);
        Assert.Equal(desktopModel.Answer, completed.Output);
        Assert.Contains("The target date is not set", completed.Output, StringComparison.Ordinal);
        Assert.Contains("Release the first version", desktopModel.LastMessages.Single(message => message.Role == "system").Content);

        var desktopStore = _desk!.Services.GetRequiredService<IConversationStore>();
        var desktopConversation = Assert.Single(await desktopStore.ListAsync("local"));
        var start = Assert.Single(await desktopStore.ReadEventsAsync(desktopConversation.Id),
            entry => entry.Type == ConversationEventType.TurnStart);
        Assert.Equal(queued.Id, start.Id);
        var receipt = JsonSerializer.Deserialize<TurnRunStart>(start.Data)!;
        Assert.Equal(queued.Id, receipt.RunId);
        Assert.Equal(project.Id, receipt.ProjectId);
    }

    [Fact]
    public async Task Pausing_a_Mobile_project_agent_cancels_the_in_flight_Desktop_generation()
    {
        var desktopModel = new HoldingRuntime();
        await OffAsync();
        _deskRuntime = desktopModel;
        await OnAsync();

        await using var mobile = new ServiceCollection()
            .AddConciergeChat(Path.Combine(_root, "mobile-cancel.db"))
            .BuildServiceProvider();
        var projects = mobile.GetRequiredService<IProjectStore>();
        var project = await projects.CreateAsync(ConciergeProject.Create("Site launch", "Publish the first site"));
        var coordinator = new ProjectAgentCoordinator(projects);
        var queued = await coordinator.QueueAsync(project.Id, new AwayProjectAgentRuntime(AWatch()),
            "Site analyst", "Review the release plan.");

        await desktopModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.PauseAsync(project.Id, queued.Id);
        var paused = await WaitForAgentStatusAsync(projects, project.Id, queued.Id, ProjectAgentStatus.Paused);

        Assert.Contains("Paused", paused.Problem, StringComparison.Ordinal);
        await desktopModel.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<ProjectAgentRun> WaitForAgentStatusAsync(
        IProjectStore projects, Guid projectId, Guid runId, ProjectAgentStatus expected)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var run = (await projects.GetAsync(projectId))?.AgentRuns.SingleOrDefault(item => item.Id == runId);
            if (run?.Status == expected)
                return run;
            await Task.Delay(25);
        }

        var final = (await projects.GetAsync(projectId))?.AgentRuns.SingleOrDefault(item => item.Id == runId);
        throw new TimeoutException($"Project-agent run did not reach {expected}; found {final?.Status}: {final?.Problem}");
    }

    private static TurnRunner Runner(IConversationStore store, IChatRuntime runtime)
    {
        var registry = new AgentToolRegistry([]);
        return new TurnRunner(store, registry, new ToolCallScheduler(registry),
            new BasicCompactionEngine(new HeuristicTokenMeter(), runtime),
            new HeadTailToolResultPruner(), new RepeatToolReminder(), new EmptySkills(),
            new ConciergeToolLoopOptions(), new BackgroundRuns(), new CapturedImages(),
            runGate: new ConversationRunGate());
    }

    private sealed class EmptySkills : ISkillRuntime
    {
        public SkillActivation? Activate(string skillId) => null;
        public string ComposeSystemPrompt(IEnumerable<string> skillIds, int budgetTokens = SkillRuntime.DefaultBudgetTokens)
            => string.Empty;
    }

    private sealed class AnswerRuntime(string answer) : IChatRuntime
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI Desktop test runtime";
        public bool IsReady => true;
        public string StatusMessage => "Ready";
        public string Answer => answer;
        public IReadOnlyList<ChatTurn> LastMessages { get; private set; } = [];

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages;
            var lines = answer.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return lines[index] + (index < lines.Length - 1 ? "\n" : string.Empty);
            }
        }
    }

    private sealed class HoldingRuntime : IChatRuntime
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI Desktop holding test runtime";
        public bool IsReady => true;
        public string StatusMessage => "Ready";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            yield break;
        }
    }

    /// <summary>
    /// **And a picture is never kept for later**, unlike a sentence. Megabytes on a small disk
    /// and a paid-for connection, and "what is this?" answered forty minutes after you walked
    /// away from the thing is not an answer anybody wanted.
    /// </summary>
    [Fact]
    public async Task But_a_picture_is_never_queued()
    {
        var watch = AWatch();

        await OffAsync();

        var looked = await watch.LookAsync(
            "what is this?", new Situation(At("06:20")), new Looked("wall.png", [1, 2, 3]));

        Assert.False(looked.Understood);
        Assert.Equal(0, watch.Waiting);

        await OnAsync();
    }

    private static DateTimeOffset At(string time)
        => DateTimeOffset.Parse(
            $"2026-09-25T{time}:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class CatalogRuntime : IChatRuntime, IChatRuntimeCapabilitySource
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI";
        public bool IsReady => false;
        public string StatusMessage => "Preparing";
        public ChatRuntimeCapabilityCatalog CapabilityCatalog { get; } = new(
            SchemaVersion: 1,
            RuntimeId: "circleai",
            RuntimeVersion: "3.6.0",
            ObservedAt: DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            Capabilities: [new("conversation", "Conversation", ChatRuntimeCapabilityState.Preparing)],
            Resources: new(12.5, 42, 12, "connected", null));

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
