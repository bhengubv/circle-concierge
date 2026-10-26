using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Components.Pages;
using Concierge.Shared.Context;
using Concierge.Shared.Planning;
using Concierge.Shared.Settings;
using Concierge.Shared.Skills;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class FeatureGapTests : IAsyncLifetime
{
    [Fact]
    public void ShippedSpriteDefinesThePhoneNavigationAndComposerIcons()
    {
        using var context = new Bunit.BunitContext();
        var sprite = context.Render<Concierge.Shared.Components.Components.CircleIconSprite>();
        foreach (var name in new[] { "menu", "mic", "camera", "send", "circle" })
            Assert.Single(sprite.FindAll($"symbol#cm-icon-{name}"));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"concierge-feature-{Guid.NewGuid():N}");
    private ServiceProvider _services = null!;
    private readonly ReplyRuntime _runtime = new();
    private FileScheduledTaskStore _schedule = null!;
    private IConversationStore Conversations => _services.GetRequiredService<IConversationStore>();
    private IKnowledgeNotebookStore Notebooks => _services.GetRequiredService<IKnowledgeNotebookStore>();
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _schedule = new FileScheduledTaskStore(Path.Combine(_root, "schedule.json"));
        var services = new ServiceCollection().AddLogging().AddConciergeChat(Path.Combine(_root, "chat.db"));
        services.AddSingleton<IScheduledTaskStore>(_schedule);
        services.AddSingleton<IKnowledgeNotebookStore>(new FileKnowledgeNotebookStore(Path.Combine(_root, "notebooks.json")));
        services.AddSingleton<IChatRuntime>(_runtime);
        services.AddSingleton<IChatRuntime>(new UnconfiguredRuntime());
        services.AddSingleton<BackgroundRuns>();
        services.AddSingleton<InteractiveToolApprovalService>();
        services.AddSingleton<ProbeTool>();
        services.AddSingleton<IAgentToolRegistry>(sp => new AgentToolRegistry([sp.GetRequiredService<ProbeTool>()]));
        services.AddScoped(sp => new TurnRunner(Conversations,
            sp.GetRequiredService<IAgentToolRegistry>(), new ToolCallScheduler(sp.GetRequiredService<IAgentToolRegistry>()),
            new BasicCompactionEngine(new HeuristicTokenMeter(), _runtime), new HeadTailToolResultPruner(),
            new RepeatToolReminder(), new EmptySkills(), new ConciergeToolLoopOptions(),
            sp.GetRequiredService<BackgroundRuns>(), new CapturedImages(), notebooks: Notebooks));
        services.AddSingleton<ScheduledWorkService>();
        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }
    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
    private ScheduledWorkService Worker => _services.GetRequiredService<ScheduledWorkService>();

    [Fact]
    public async Task Due_work_runs_once_and_keeps_the_answer_in_history_and_schedule_after_reload()
    {
        _schedule.Schedule("Plan tomorrow", DateTimeOffset.UtcNow.AddMinutes(-1));
        await Worker.RunDueAsync(DateTimeOffset.UtcNow);
        await Worker.RunDueAsync(DateTimeOffset.UtcNow);
        var saved = Assert.Single(new FileScheduledTaskStore(Path.Combine(_root, "schedule.json")).All());
        Assert.Equal("Completed", saved.State);
        Assert.Equal("Here is your plan.", saved.Result);
        Assert.Equal(1, _runtime.Calls);
        Assert.Contains(await Conversations.ReadEventsAsync(saved.ConversationId!.Value),
            e => e.Type == ConversationEventType.AssistantMessage && e.Data == saved.Result);
    }

    [Fact]
    public async Task Repeating_work_reuses_the_thread_but_starts_a_new_run()
    {
        _schedule.Schedule("Daily plan", DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.FromDays(1));
        await Worker.RunDueAsync(DateTimeOffset.UtcNow);
        await Worker.RunDueAsync(DateTimeOffset.UtcNow.AddDays(2));
        var saved = Assert.Single(_schedule.All());
        Assert.Equal("Scheduled", saved.State);
        Assert.Equal(2, _runtime.Calls);
        Assert.Single(await Conversations.ListAsync("local"));
        var starts = (await Conversations.ReadEventsAsync(saved.ConversationId!.Value)).Where(e => e.Type == ConversationEventType.TurnStart).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.NotEqual(starts[0].Id, starts[1].Id);
    }

    [Fact]
    public async Task Crash_after_receipt_before_schedule_save_replays_without_repeating_work()
    {
        var queued = _schedule.Schedule("Do it once", DateTimeOffset.UtcNow.AddMinutes(-1));
        var running = _schedule.Begin(queued.Id, DateTimeOffset.UtcNow)!;
        await Conversations.StartWithIdAsync(running.ConversationId!.Value, "local");
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TurnRunner>().RunAsync(new TurnRequest(
            running.ConversationId.Value, running.Description, _runtime, RunId: running.RunId));
        await Worker.RunDueAsync(DateTimeOffset.UtcNow);
        Assert.Equal(1, _runtime.Calls);
        Assert.Equal("Completed", Assert.Single(_schedule.All()).State);
    }

    [Fact]
    public async Task Ask_first_does_not_inherit_act_freely_from_another_conversation()
    {
        _runtime.UseTool = true;
        var approvals = _services.GetRequiredService<InteractiveToolApprovalService>();
        approvals.AllowWithoutAsking = true;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        approvals.PendingChanged += (_, _) => { if (approvals.Pending.Count > 0) asked.TrySetResult(); };
        _schedule.Schedule("Make a change", DateTimeOffset.UtcNow.AddMinutes(-1));
        var run = Worker.RunDueAsync(DateTimeOffset.UtcNow);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, _services.GetRequiredService<ProbeTool>().Changes);
        approvals.Answer(Assert.Single(approvals.Pending).Id, ToolApprovalDecision.Allowed);
        await run;
        Assert.Equal(1, _services.GetRequiredService<ProbeTool>().Changes);
    }

    [Fact]
    public async Task Pausing_a_running_task_stops_waiting_approval_without_changing_anything()
    {
        _runtime.UseTool = true;
        var approvals = _services.GetRequiredService<InteractiveToolApprovalService>();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        approvals.PendingChanged += (_, _) => { if (approvals.Pending.Count > 0) asked.TrySetResult(); };
        var task = _schedule.Schedule("Make a change", DateTimeOffset.UtcNow.AddMinutes(-1));
        var run = Worker.RunDueAsync(DateTimeOffset.UtcNow);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Worker.Stop(task.Id);
        await run;
        Assert.False(Assert.Single(_schedule.All()).Enabled);
        Assert.Equal(0, _services.GetRequiredService<ProbeTool>().Changes);
        await Worker.RunDueAsync(DateTimeOffset.UtcNow);
        Assert.Equal(0, _services.GetRequiredService<ProbeTool>().Changes);
    }

    [Fact]
    public async Task Notebook_sources_survive_restart_and_updates_reach_the_next_question_only_in_linked_threads()
    {
        var conversation = await Conversations.StartAsync("local", "Homework");
        var notebook = Notebooks.Save(new KnowledgeNotebook(Guid.NewGuid(), "Science", "Cite your sources",
            [new NotebookSource(Guid.NewGuid(), "Lesson notes", "The agreed answer is forty-two.")], [conversation.Id], DateTimeOffset.UtcNow));
        Assert.Single(new FileKnowledgeNotebookStore(Path.Combine(_root, "notebooks.json")).All());
        using var scope = _services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<TurnRunner>();
        await runner.RunAsync(new TurnRequest(conversation.Id, "What is the agreed answer?", _runtime));
        Assert.Contains("forty-two", _runtime.LastPrompt);
        Assert.Contains("Lesson notes", _runtime.LastPrompt);
        Notebooks.Save(notebook with { Sources = [notebook.Sources[0] with { Text = "The agreed answer is fifty." }] });
        await runner.RunAsync(new TurnRequest(conversation.Id, "And now?", _runtime));
        Assert.Contains("fifty", _runtime.LastPrompt);
        Assert.DoesNotContain("forty-two", _runtime.LastPrompt);
        var other = await Conversations.StartAsync("local", "Unrelated");
        await runner.RunAsync(new TurnRequest(other.Id, "Hello", _runtime));
        Assert.DoesNotContain("Lesson notes", _runtime.LastPrompt);
    }

    [Fact]
    public void Corrupt_schedule_is_not_silently_replaced_and_stale_notebook_edits_are_rejected()
    {
        var path = Path.Combine(_root, "schedule.json");
        File.WriteAllText(path, "{broken");
        Assert.Throws<JsonException>(() => _schedule.Schedule("New", DateTimeOffset.UtcNow));
        Assert.Equal("{broken", File.ReadAllText(path));
        var first = Notebooks.Save(new KnowledgeNotebook(Guid.NewGuid(), "Notes", "", [], [], DateTimeOffset.UtcNow));
        Notebooks.Save(first with { Name = "Updated" });
        Assert.Throws<InvalidOperationException>(() => Notebooks.Save(first with { Name = "Stale" }));
    }

    [Fact]
    public async Task Library_previews_real_saved_output_and_searches_the_catalogue()
    {
        var conversation = await Conversations.StartAsync("local", "Launch planning");
        var source = await Conversations.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "A saved business plan.");
        await Conversations.AppendEventAsync(conversation.Id, ConversationEventType.ArtifactCreated,
            JsonSerializer.Serialize(new ConversationArtifactRecord(Guid.NewGuid(), "Business plan", "text/plain", source.Id)));
        using var context = new BunitContext();
        context.Services.AddSingleton(Conversations);
        context.Services.AddSingleton(_services.GetRequiredService<IProjectStore>());
        var cut = context.Render<Library>();
        cut.WaitForAssertion(() => Assert.Contains("Business plan", cut.Markup));
        cut.FindAll("button").Single(b => b.TextContent == "Preview output").Click();
        cut.WaitForAssertion(() => Assert.Contains("A saved business plan.", cut.Find(".library-preview").TextContent));
        cut.Find("input[type=search]").Input("does not exist");
        cut.WaitForAssertion(() => Assert.Contains("Nothing matches", cut.Markup));
    }

    [Fact]
    public async Task Library_renders_only_matching_bounded_raster_media_as_inline_previews()
    {
        var conversation = await Conversations.StartAsync("local", "Media output");
        const string pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/pWQAAAAASUVORK5CYII=";
        var source = await Conversations.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, pixel);
        await Conversations.AppendEventAsync(conversation.Id, ConversationEventType.ArtifactCreated,
            JsonSerializer.Serialize(new ConversationArtifactRecord(Guid.NewGuid(), "Pixel", "image/png", source.Id)));
        using var context = new BunitContext();
        context.Services.AddSingleton(Conversations);
        context.Services.AddSingleton(_services.GetRequiredService<IProjectStore>());
        var cut = context.Render<Library>();

        cut.WaitForAssertion(() => Assert.Contains("Pixel", cut.Markup));
        cut.FindAll("button").Single(button => button.TextContent == "Preview output").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(pixel, cut.Find("img.library-media-preview").GetAttribute("src"));
            Assert.Empty(cut.FindAll("pre.library-preview"));
        });
    }

    [Fact]
    public async Task Library_keeps_unapproved_media_types_as_text_instead_of_embedding_them()
    {
        var conversation = await Conversations.StartAsync("local", "Untrusted output");
        const string payload = "data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSkvPg==";
        var source = await Conversations.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, payload);
        await Conversations.AppendEventAsync(conversation.Id, ConversationEventType.ArtifactCreated,
            JsonSerializer.Serialize(new ConversationArtifactRecord(Guid.NewGuid(), "Untrusted", "image/svg+xml", source.Id)));
        using var context = new BunitContext();
        context.Services.AddSingleton(Conversations);
        context.Services.AddSingleton(_services.GetRequiredService<IProjectStore>());
        var cut = context.Render<Library>();

        cut.WaitForAssertion(() => Assert.Contains("Untrusted", cut.Markup));
        cut.FindAll("button").Single(button => button.TextContent == "Preview output").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("img.library-media-preview"));
            Assert.Contains(payload, cut.Find("pre.library-preview").TextContent);
        });
    }

    [Fact]
    public void File_activity_records_command_results_without_claiming_file_side_effects()
    {
        var run = Guid.NewGuid();
        var events = new[]
        {
            new ConversationEvent { Id = run, Seq = 0, Type = ConversationEventType.TurnStart },
            new ConversationEvent { Seq = 1, Type = ConversationEventType.ToolCall, Data = "write_file {\"path\":\"plan.txt\"}" },
            new ConversationEvent { Seq = 2, Type = ConversationEventType.ToolResult, Data = ToolCallProtocol.FormatResult("write_file", new AgentToolResult(false, "", "Denied")) },
            new ConversationEvent { Seq = 3, Type = ConversationEventType.ToolCall, Data = "read_file {\"path\":\"notes.txt\"}" },
            new ConversationEvent { Seq = 4, Type = ConversationEventType.ToolResult, Data = ToolCallProtocol.FormatResult("read_file", new AgentToolResult(true, "notes", null)) },
            new ConversationEvent { Seq = 5, Type = ConversationEventType.ToolCall, Data = "write_file {\"path\":\"next.txt\"}" },
            new ConversationEvent { Seq = 6, Type = ConversationEventType.ToolCall, Data = "run_command {\"command\":\"dotnet test\"}" },
            new ConversationEvent { Seq = 7, Type = ConversationEventType.ToolResult, Data = ToolCallProtocol.FormatResult("run_command", new AgentToolResult(true, "Tests passed", null)) },
            new ConversationEvent { Seq = 8, Type = ConversationEventType.ToolCall, Data = "run_command {\"command\":\"git push\"}" },
            new ConversationEvent { Seq = 9, Type = ConversationEventType.ToolResult, Data = ToolCallProtocol.FormatResult("run_command", new AgentToolResult(false, "", "Approval denied")) },
        };
        var rows = FileActivityProjection.From(events);
        Assert.Equal(new[] { "Failed or refused", "Succeeded", "No result recorded", "Succeeded", "Failed or refused" }, rows.Select(r => r.Outcome));
        Assert.All(rows, row => Assert.Equal(run, row.RunId));
        Assert.Equal(new[] { "next.txt", "dotnet test", "git push" }, rows.Skip(2).Select(row => row.Path));
        Assert.Equal(new[] { "Command", "Command" }, rows.Skip(3).Select(row => row.Operation));
    }

    [Fact]
    public void Notebook_editor_saves_sources_and_instructions_from_its_visible_controls()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton(Notebooks);
        context.Services.AddSingleton(Conversations);
        var cut = context.Render<Concierge.Shared.Components.Pages.Notebooks>();
        cut.FindAll("button").Single(b => b.TextContent == "New notebook").Click();
        cut.Find("#notebook-name").Change("Product launch");
        cut.Find("#notebook-instructions").Change("Cite the launch notes.");
        cut.Find("#notebook-source-name").Change("Launch notes");
        cut.Find("#notebook-source-text").Change("Launch is Thursday. Budget is 300.");
        cut.FindAll("button").Single(b => b.TextContent == "Add source").Click();
        cut.FindAll("button").Single(b => b.TextContent == "Save notebook").Click();
        var saved = Assert.Single(Notebooks.All());
        Assert.Equal("Product launch", saved.Name);
        Assert.Equal("Cite the launch notes.", saved.Instructions);
        Assert.Equal("Launch is Thursday. Budget is 300.", Assert.Single(saved.Sources).Text);
        Assert.Contains("Notebook saved", cut.Markup);
    }

    [Fact]
    public async Task Scheduled_page_creates_runs_and_displays_a_result_from_user_controls()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IScheduledTaskStore>(_schedule);
        context.Services.AddSingleton(Worker);
        context.Services.AddSingleton(_services.GetRequiredService<BackgroundRuns>());
        var cut = context.Render<Scheduled>();
        cut.Find("#schedule-description").Change("Summarize my day");
        cut.FindAll("button").Single(b => b.TextContent == "Schedule").Click();
        Assert.Equal(ToolPermissionMode.AskFirst, Assert.Single(_schedule.All()).PermissionMode);
        cut.FindAll("button").Single(b => b.TextContent == "Run now").Click();
        await Worker.RunDueAsync(DateTimeOffset.UtcNow);
        // Refresh is a timer in the actual page, not a test-only reload hook.
        cut.WaitForAssertion(() => Assert.Contains("Here is your plan.", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Contains("Open progress and result", cut.Markup);
    }

    [Fact]
    public async Task Chat_cannot_schedule_without_approval_or_escalate_the_future_action_mode()
    {
        var args = JsonNode.Parse(JsonSerializer.Serialize(new { instructions = "Plan a launch", dueAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"), permission = "ActFreely" }));
        var blocked = new ScheduleCreateTool(_schedule, UnavailableToolApprovalService.Instance);
        Assert.False((await blocked.InvokeAsync(args)).Success);
        Assert.Empty(_schedule.All());
        var allowed = new ScheduleCreateTool(_schedule, new AllowApproval());
        Assert.True((await allowed.InvokeAsync(args)).Success);
        Assert.Equal(ToolPermissionMode.AskFirst, Assert.Single(_schedule.All()).PermissionMode);
        args!["dueAt"] = "2027-01-01T09:00:00";
        Assert.False((await allowed.InvokeAsync(args)).Success);
        Assert.Single(_schedule.All());
    }

    private sealed class AllowApproval : IToolApprovalService
    {
        public ValueTask<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ToolApprovalDecision.Allowed);
    }

    [Fact]
    public async Task Malformed_tool_requests_are_reported_corrected_once_and_only_then_executed()
    {
        _runtime.Replies = [
            "```tool-call {\"name\":\"probe_change\",\"arguments\":{} ```",
            "```tool-call {\"name\":\"probe_change\",\"arguments\":{}} ```",
            "The change was completed."
        ];
        _services.GetRequiredService<InteractiveToolApprovalService>().AllowWithoutAsking = true;
        var conversation = await Conversations.StartAsync("local", "Tool format repair");
        using var scope = _services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<TurnRunner>().RunAsync(new TurnRequest(conversation.Id, "Make the change", _runtime));
        Assert.Null(result.Stopped);
        Assert.Equal(1, _services.GetRequiredService<ProbeTool>().Changes);
        var events = await Conversations.ReadEventsAsync(conversation.Id);
        Assert.Contains(events, e => e.Type == ConversationEventType.ToolResult && e.Data.Contains("invalid tool request"));
        Assert.Single(events.Where(e => e.Type == ConversationEventType.ToolCall));
    }

    [Fact]
    public async Task Repeated_invalid_tool_requests_stop_visibly_without_running_anything()
    {
        _runtime.Replies = ["```tool-call {\"name\":123,\"arguments\":{}} ```"];
        var conversation = await Conversations.StartAsync("local", "Invalid tool format");
        using var scope = _services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<TurnRunner>().RunAsync(new TurnRequest(conversation.Id, "Make the change", _runtime));
        Assert.Contains("correction attempt also failed", result.Stopped);
        Assert.Equal(2, _runtime.Calls);
        Assert.Equal(0, _services.GetRequiredService<ProbeTool>().Changes);
    }

    [Fact]
    public void Local_state_does_not_overwrite_another_process_write_and_notebooks_cannot_share_a_thread()
    {
        var notebook = Notebooks.Save(new KnowledgeNotebook(Guid.NewGuid(), "First", "", [], [], DateTimeOffset.UtcNow));
        var conversationId = Guid.NewGuid();
        notebook = Notebooks.LinkConversation(notebook.Id, conversationId);
        var other = Notebooks.Save(new KnowledgeNotebook(Guid.NewGuid(), "Second", "", [], [], DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => Notebooks.LinkConversation(other.Id, conversationId));
        using (File.Open(Path.Combine(_root, "notebooks.json.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => Notebooks.Save(notebook with { Name = "Should not overwrite" }));
        Assert.Contains(Notebooks.All(), n => n.Name == "First");
        using (File.Open(Path.Combine(_root, "schedule.json.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => _schedule.Schedule("Should not overwrite", DateTimeOffset.UtcNow));
        Assert.Empty(_schedule.All());
    }

    private sealed class UnconfiguredRuntime : IChatRuntime
    {
        public string Id => "unconfigured-provider";
        public string EngineLabel => "Not configured";
        public bool IsReady => false;
        public string StatusMessage => "No credentials";
        public IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The scheduler chose an unavailable provider.");
    }

    private sealed class EmptySkills : ISkillRuntime
    {
        public SkillActivation? Activate(string skillId) => null;
        public string ComposeSystemPrompt(IEnumerable<string> skillIds, int budgetTokens = SkillRuntime.DefaultBudgetTokens) => "";
    }
    private sealed class ReplyRuntime : IChatRuntime
    {
        public string Id => "circleai";
        public string EngineLabel => "Acceptance runtime";
        public bool IsReady => true;
        public string StatusMessage => "Ready";
        public int Calls;
        public bool UseTool;
        public string[]? Replies;
        public string LastPrompt = "";
        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastPrompt = string.Join("\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
            await Task.Yield();
            yield return Replies is { Length: > 0 } replies ? replies[Math.Min(Calls - 1, replies.Length - 1)]
                : UseTool && Calls == 1 ? "```tool-call\n{\"name\":\"probe_change\",\"arguments\":{}}\n```" : "Here is your plan.";
        }
    }
    private sealed class ProbeTool(InteractiveToolApprovalService approval) : IAgentTool
    {
        public string Name => "probe_change";
        public string Description => "A meaningful approval test.";
        public bool IsReadOnly => false;
        public JsonNode? ArgumentsSchema => null;
        public int Changes;
        public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var answer = await approval.RequestAsync(new ToolApprovalRequest(Name, "Make a change", ConciergeToolRisk.Medium), cancellationToken);
            if (answer != ToolApprovalDecision.Allowed) return new(false, "", "Not approved");
            Changes++; return new(true, "Changed", null);
        }
    }
}
