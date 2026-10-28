using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The thread: what was said, and what the assistant actually did.
///
/// The design decision these defend is the one that makes this product legible —
/// a tool result is not something anybody said. It used to render as a chat
/// bubble headed "tool", which put the machinery in the same voice as the person
/// and buried the one thing Concierge has that a chatbot does not: you can see
/// the work. It is now a quiet chip with the output behind it.
///
/// Synchronous by design; see WorkspaceHandoffTests for why.
/// </summary>
public sealed class WorkspaceThreadTests : BunitContext
{
    private IConversationStore Compose()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"thread-{Guid.NewGuid():N}.db");
        Services.AddLogging();
        Services.AddConciergeCore();

        // Session state defaults to the real per-user file. A test that reads
        // it depends on whatever the app last did, and a test that writes it
        // changes the user's app state — both happened before this line.
        // Same reason as the session file: the real list is the user's, and a
        // test that reads or writes it depends on what the app last did.
        Services.AddSingleton(_ => new Concierge.Shared.Skills.UserSkillFolders(
            Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}.json")));

        Services.AddSingleton<Concierge.Shared.Session.ISessionState>(
            new Concierge.Shared.Session.FileSessionState(
                Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid():N}.json")));
        Services.AddConciergeChat(dbPath);
        Services.AddSingleton<IEnumerable<IChatRuntime>>(_ => Array.Empty<IChatRuntime>());
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeTools(Services);
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeRuntime(Services);
        Concierge.Shared.Diagrams.ConciergeDiagramsServiceCollectionExtensions.AddConciergeDiagrams(Services);
        Services.AddSingleton<IEnumerable<Concierge.Shared.Media.IImageRuntime>>(
            _ => Array.Empty<Concierge.Shared.Media.IImageRuntime>());
        JSInterop.Mode = JSRuntimeMode.Loose;

        using (var db = Services.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>().CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        return Services.GetRequiredService<IConversationStore>();
    }

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> Open(Guid id)
    {
        var rendered = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>(ps => ps.Add(p => p.ConversationId, id));
        rendered.WaitForState(() => rendered.FindAll(".ws[aria-busy='false']").Count > 0, TimeSpan.FromSeconds(5));
        return rendered;
    }

    // ── What was said ─────────────────────────────────────────────────────

    [Fact]
    public void A_turn_is_a_name_and_some_text_not_a_bubble()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Tidy the notes").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "Tidy up the notes folder.")
             .GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        var message = cut.Find("article.msg");
        Assert.Equal("You", message.QuerySelector(".msg-who")!.TextContent.Trim());
        Assert.Contains("Tidy up the notes folder.", message.QuerySelector(".msg-body")!.TextContent);
    }

    [Fact]
    public void An_assistant_reply_renders_through_the_prose_component()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Tidy the notes").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage,
            "Thirty-four files.\n\nSix are duplicates.").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        // Two paragraphs, which is the AssistantContent behaviour — proof the
        // thread renders replies through it rather than dumping the raw string.
        Assert.Equal(2, cut.FindAll("article.msg .assistant-markdown p").Count);
    }

    [Fact]
    public void Turns_appear_in_the_order_they_happened()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Ordering").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "First thing").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "Second thing").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        var bodies = cut.FindAll("article.msg .msg-body").Select(e => e.TextContent.Trim()).ToArray();
        Assert.Equal(2, bodies.Length);
        Assert.StartsWith("First thing", bodies[0]);
        Assert.StartsWith("Second thing", bodies[1]);
    }

    [Fact]
    public void Opening_a_thread_restores_its_durable_plan_snapshot()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Interrupted work").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TodoWrite,
            System.Text.Json.JsonSerializer.Serialize(new TurnPlanSnapshot(
                Guid.NewGuid(), ["Inspect the project", "Build a first version", "Review the result"],
                Done: 2, Revisions: 0, UpdatedAt: DateTimeOffset.UtcNow))).GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        Assert.Equal("2 of 3", cut.Find(".plan-count").TextContent.Trim());
        var steps = cut.FindAll(".plan-step").Select(step => step.TextContent.Trim()).ToArray();
        Assert.Equal(["Inspect the project", "Build a first version", "Review the result"], steps);
        Assert.Equal(2, cut.FindAll(".plan-step.is-done").Count);
    }

    [Fact]
    public void Opening_a_thread_with_an_unclosed_run_shows_that_it_was_interrupted()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Interrupted work").GetAwaiter().GetResult();
        var runId = Guid.NewGuid();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new { RunId = runId, Fingerprint = "test" }),
            eventId: runId).GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        cut.WaitForAssertion(() => Assert.Contains("This run stopped before it finished", cut.Find(".run-recovery").TextContent));
        Assert.Contains("did not automatically repeat it", cut.Find(".run-recovery").TextContent);
    }

    [Fact]
    public void Interrupted_run_names_tool_calls_without_a_durable_result()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Uncertain tool action").GetAwaiter().GetResult();
        var runId = Guid.NewGuid();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new { RunId = runId, Fingerprint = "test" }),
            eventId: runId).GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolCall,
            "read_file {\"path\":\"notes.md\"}").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult,
            ToolCallProtocol.FormatResult("read_file", new AgentToolResult(true, "old notes"))).GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolCall,
            "write_file {\"path\":\"notes.md\"}").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        cut.WaitForAssertion(() => Assert.Contains("outcome of these recorded actions is unknown",
            cut.Find(".run-recovery").TextContent));
        Assert.Contains("write_file", cut.Find(".run-recovery").TextContent);
        Assert.Contains("Check whether they took effect before retrying", cut.Find(".run-recovery").TextContent);
        Assert.DoesNotContain("read_file", cut.Find(".run-recovery").TextContent);
    }

    [Fact]
    public void Opening_a_thread_with_a_live_run_does_not_label_it_interrupted()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Live work").GetAwaiter().GetResult();
        var runId = Guid.NewGuid();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new { RunId = runId, Fingerprint = "test" }),
            eventId: runId).GetAwaiter().GetResult();
        using var cancellation = new CancellationTokenSource();
        Services.GetRequiredService<BackgroundRuns>().Started(conversation.Id, cancellation);

        var cut = Open(conversation.Id);

        Assert.Contains("still working on this device", cut.Find(".run-recovery").TextContent);
        Assert.DoesNotContain("stopped before it finished", cut.Find(".run-recovery").TextContent);
        Services.GetRequiredService<BackgroundRuns>().Finished(conversation.Id);
    }

    [Fact]
    public async Task Active_run_exposes_a_stop_control_that_cancels_the_run()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Work in progress").GetAwaiter().GetResult();
        using var cancellation = new CancellationTokenSource();
        Services.GetRequiredService<BackgroundRuns>().Started(conversation.Id, cancellation);

        var cut = Open(conversation.Id);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button[aria-label='Stop this run']")));
        await cut.InvokeAsync(() => cut.Find("button[aria-label='Stop this run']").Click());

        cut.WaitForAssertion(() => Assert.True(cancellation.IsCancellationRequested));
        Assert.Contains("Stopping this run", cut.Markup);
        Services.GetRequiredService<BackgroundRuns>().Finished(conversation.Id);
    }

    [Fact]
    public void A_closed_run_does_not_show_interruption_status()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Completed work").GetAwaiter().GetResult();
        var runId = Guid.NewGuid();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new { RunId = runId, Fingerprint = "test" }),
            eventId: runId).GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnEnd,
            System.Text.Json.JsonSerializer.Serialize(new TurnRunReceipt(
                runId, "completed", "Done", "test runtime", [], 0, null))).GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        Assert.Empty(cut.FindAll(".run-recovery"));
    }

    [Fact]
    public async Task A_confirmed_stop_replaces_the_stopping_hint()
    {
        var store = Compose();
        var conversation = await store.StartAsync("local", title: "Stop confirmation");
        var runId = Guid.NewGuid();
        await store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new TurnRunStart(runId, "test")), eventId: runId);
        using var cancellation = new CancellationTokenSource();
        var runs = Services.GetRequiredService<BackgroundRuns>();
        runs.Started(conversation.Id, cancellation);
        var cut = Open(conversation.Id);
        await cut.InvokeAsync(() => cut.Find("button[aria-label='Stop this run']").Click());
        Assert.Contains("Stopping this run", cut.Markup);
        await store.AppendEventAsync(conversation.Id, ConversationEventType.TurnEnd,
            System.Text.Json.JsonSerializer.Serialize(new TurnRunReceipt(runId, "cancelled", null, "test", [], 0, null)));
        runs.Finished(conversation.Id);
        cut.WaitForAssertion(() => Assert.Contains("Run stopped. Your work is saved.", cut.Markup));
        Assert.DoesNotContain("Stopping this run", cut.Markup);
    }

    [Theory]
    [InlineData("cancelled", "This run was stopped")]
    [InlineData("failed", "This run failed")]
    public void Returning_to_an_unsuccessful_terminal_run_shows_its_outcome(string status, string expected)
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Stopped work").GetAwaiter().GetResult();
        var runId = Guid.NewGuid();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new TurnRunStart(runId, "test")),
            eventId: runId).GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "Help me").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnEnd,
            System.Text.Json.JsonSerializer.Serialize(new TurnRunReceipt(
                runId, status, null, "test runtime", [], 0, null))).GetAwaiter().GetResult();

        var cut = Open(conversation.Id);
        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Find(".run-recovery").TextContent));
        Assert.Contains("recorded activity are saved", cut.Find(".run-recovery").TextContent);
        Assert.Empty(cut.FindAll("button[aria-label='Stop this run']"));
    }

    [Fact]
    public void An_unanswered_approval_is_identified_when_the_run_is_restored()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Approval recovery").GetAwaiter().GetResult();
        var runId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new { RunId = runId, Fingerprint = "test" }),
            eventId: runId).GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ApprovalAsked,
            System.Text.Json.JsonSerializer.Serialize(new ConversationApprovalEvent(
                approvalId, runId, "write_file", "replace the launch notes", ConciergeToolRisk.High,
                "proposed diff", DateTimeOffset.UtcNow))).GetAwaiter().GetResult();
        Assert.False(Services.GetRequiredService<BackgroundRuns>().IsRunning(conversation.Id));

        var cut = Open(conversation.Id);

        cut.WaitForAssertion(() => Assert.Contains("waiting for permission to replace the launch notes",
            cut.Find(".run-recovery").TextContent));
        Assert.Contains("was not automatically approved", cut.Find(".run-recovery").TextContent);
        Assert.Contains("proposed diff", cut.Find(".approval-history").TextContent);
        Assert.Contains("not automatically approved", cut.Find(".approval-history").TextContent);
    }

    [Fact]
    public void Opening_a_thread_restores_the_permission_decision_and_request_detail()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Permission history").GetAwaiter().GetResult();
        var runId = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            System.Text.Json.JsonSerializer.Serialize(new { RunId = runId, Fingerprint = "test" }),
            eventId: runId).GetAwaiter().GetResult();
        var request = new ConversationApprovalEvent(
            approvalId, runId, "write_file", "replace the launch notes", ConciergeToolRisk.High,
            "proposed launch-notes diff", DateTimeOffset.UtcNow);
        store.AppendEventAsync(conversation.Id, ConversationEventType.ApprovalAsked,
            System.Text.Json.JsonSerializer.Serialize(request), eventId: approvalId).GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ApprovalDecided,
            System.Text.Json.JsonSerializer.Serialize(request with { Decision = ToolApprovalDecision.Denied }))
            .GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        cut.WaitForAssertion(() => Assert.Contains("Declined", cut.Find(".approval-history").TextContent));
        Assert.Contains("replace the launch notes", cut.Find(".approval-history").TextContent);
        Assert.Contains("proposed launch-notes diff", cut.Find(".approval-history").TextContent);
    }

    // ── What it did ───────────────────────────────────────────────────────

    /// <summary>
    /// The decision this whole file exists for: a tool result is a chip, not a
    /// turn. If this fails, the machinery is speaking in the assistant's voice
    /// again.
    /// </summary>
    [Fact]
    public void A_tool_result_is_a_chip_and_never_a_turn()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "With tools").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult,
            "Listed the folder\n34 files found").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        var chip = cut.Find("button.chip");
        Assert.Equal("Listed the folder", chip.QuerySelector(".chip-what")!.TextContent.Trim());

        // And nothing rendered it as something somebody said.
        Assert.Empty(cut.FindAll("article.msg"));
        Assert.Empty(cut.FindAll(".msg-who"));
    }

    [Fact]
    public void A_completed_tool_receipt_names_its_outcome_and_hides_detail_until_opened()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "With a receipt").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult,
            ToolCallProtocol.FormatResult("read_file", new AgentToolResult(true, "42 lines read")))
            .GetAwaiter().GetResult();

        var cut = Open(conversation.Id);
        cut.WaitForAssertion(() => Assert.Contains("Read file · Completed", cut.Find("button.chip").TextContent));
        Assert.Equal("true", cut.Find(".chips[data-tool-success]").GetAttribute("data-tool-success"));
        Assert.Empty(cut.FindAll("pre.chip-out"));

        cut.Find("button.chip").Click();
        cut.WaitForAssertion(() => Assert.Contains("42 lines read", cut.Find("pre.chip-out").TextContent));
    }

    [Fact]
    public void A_tool_request_is_shown_as_activity_rather_than_an_intermediate_answer()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Tool work").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage,
            "I cannot browse this folder.\n```tool-call\n{\"name\":\"list_files\",\"arguments\":{\"path\":\"work\"}}\n```")
            .GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult,
            ToolCallProtocol.FormatResult("list_files", new AgentToolResult(true, "orders.csv")))
            .GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage,
            "I found orders.csv.").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        cut.WaitForAssertion(() => Assert.Contains("List files · Completed", cut.Find("button.chip").TextContent));
        Assert.DoesNotContain("I cannot browse this folder", cut.Find(".ws-thread").TextContent);
        Assert.Single(cut.FindAll("article.msg"));
        Assert.Contains("I found orders.csv.", cut.Find("article.msg").TextContent);
    }

    [Fact]
    public void A_failed_tool_receipt_says_it_needs_attention()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Failed receipt").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult,
            ToolCallProtocol.FormatResult("web_search", new AgentToolResult(false, string.Empty, "Search is not configured")))
            .GetAwaiter().GetResult();

        var cut = Open(conversation.Id);
        cut.WaitForAssertion(() => Assert.Contains("Web search · Needs attention", cut.Find("button.chip").TextContent));
        Assert.Equal("false", cut.Find(".chips[data-tool-success]").GetAttribute("data-tool-success"));
        cut.Find("button.chip").Click();
        cut.WaitForAssertion(() => Assert.Contains("Search is not configured", cut.Find("pre.chip-out").TextContent));
    }

    [Fact]
    public void A_chip_is_collapsed_until_it_is_asked()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "With tools").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult,
            "Read 6 files\nthe detailed output nobody wants by default").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        // Waited for rather than read once. The thread is loaded in
        // OnInitializedAsync, so on a loaded machine the first render can carry no
        // chip at all — the third test in this suite to be wrong in exactly this
        // way, each one passing on retry, which is the worst way for a test to be
        // wrong.
        cut.WaitForAssertion(() =>
            Assert.Equal("false", cut.Find("button.chip").GetAttribute("aria-expanded")));

        Assert.Empty(cut.FindAll("pre.chip-out"));

        cut.Find("button.chip").Click();

        // Waited for on this side too. Only the *first* render was waited for, and
        // the one after the click was read straight away — so the test held for a
        // race it had already been fixed for once, three lines further down. Under
        // load it failed here instead, which read as a different bug.
        cut.WaitForAssertion(() =>
            Assert.Equal("true", cut.Find("button.chip").GetAttribute("aria-expanded")));

        cut.WaitForAssertion(() => Assert.Contains(
            "the detailed output nobody wants by default", cut.Find("pre.chip-out").TextContent));
    }

    /// <summary>
    /// A one-line result has nothing behind it, so it must not pretend to. The
    /// chevron is a promise that there is more.
    /// </summary>
    [Fact]
    public void A_chip_with_nothing_behind_it_offers_no_chevron()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "With tools").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult, "Checked the clock")
             .GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        Assert.Empty(cut.FindAll("button.chip .chip-more"));
    }

    [Fact]
    public void Work_and_conversation_sit_together_in_one_stream()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Mixed").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "Tidy the folder").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult, "Listed the folder\n34 files").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "Thirty-four files.").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        Assert.Equal(2, cut.FindAll("article.msg").Count);
        Assert.Single(cut.FindAll("button.chip"));
    }

    // ── The frame around it ───────────────────────────────────────────────

    [Fact]
    public void Opening_a_conversation_replaces_the_empty_state()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Tidy the notes").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        Assert.Equal("Tidy the notes", cut.Find(".ws-title").TextContent.Trim());
        Assert.NotNull(cut.Find(".empty"));
        Assert.Empty(cut.FindAll(".ws-thread"));
    }

    [Fact]
    public void The_thread_is_named_at_the_top()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "Tidy the notes").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        Assert.Equal("Tidy the notes", cut.Find(".ws-title").TextContent.Trim());
    }

    /// <summary>
    /// With nothing open the top bar names the app, not a conversation that is
    /// not there.
    /// </summary>
    [Fact]
    public void With_nothing_open_the_top_bar_names_the_app()
    {
        Compose();

        var cut = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();

        Assert.Equal("Concierge", cut.Find(".ws-title").TextContent.Trim());
        Assert.NotNull(cut.Find(".empty"));
    }
}
