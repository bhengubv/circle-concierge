using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// What the event log must do (parity features 26, 27, 40): be the single ordered record
/// everything else is derived from.
/// </summary>
/// <remarks>
/// <para>
/// The store today keeps <c>(role, content)</c> rows, and the tool loop writes tool results
/// as <b>user</b> turns — so on reload nothing separates what the model did from what the
/// person typed. That is wrong data, not a missing feature.
/// </para>
/// <para>
/// The log is also the unit that travels: an append-only ordered stream is what devices that
/// are rarely connected need in order to converge. These tests pin the ordering and
/// derivation guarantees the mesh will depend on.
/// </para>
/// </remarks>
public sealed class ConversationEventLogTests : IAsyncLifetime
{
    [Fact]
    public async Task Compaction_reloads_the_summary_and_retained_question_before_the_next_answer()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendAsync(conversation.Id, "user", "Old details");
        await _store.AppendAsync(conversation.Id, "user", "Keep this question");
        var snapshot = new ConversationCompactionSnapshot(1,
            [new("system", "Summary of earlier conversation: Keep the decision"), new("user", "Keep this question")], 100, 1);
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.CompactionReplace,
            System.Text.Json.JsonSerializer.Serialize(snapshot));
        await _store.AppendAsync(conversation.Id, "assistant", "The next answer");
        var reopened = new ConversationStore(_provider.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>());
        var turns = await reopened.DeriveMessagesAsync(conversation.Id);
        Assert.Equal(new[] { "Summary of earlier conversation: Keep the decision", "Keep this question", "The next answer" }, turns.Select(t => t.Content));
    }

    [Fact]
    public async Task Legacy_compaction_marker_cannot_erase_the_only_copy_of_the_conversation()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendAsync(conversation.Id, "user", "Still here");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.CompactionReplace, "Summarised earlier conversation");
        Assert.Equal("Still here", Assert.Single(await _store.DeriveMessagesAsync(conversation.Id)).Content);
    }
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"concierge-eventlog-{Guid.NewGuid():N}.db");

    private ServiceProvider _provider = null!;
    private IConversationStore _store = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection().AddConciergeChat(_databasePath).BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = _provider.GetRequiredService<IConversationStore>();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Disposable temp file.
        }
    }

    // ── Ordering ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_event_in_a_conversation_is_sequence_zero()
    {
        var conversation = await _store.StartAsync("local");

        var appended = await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "hello");

        Assert.Equal(0, appended.Seq);
    }

    [Fact]
    public async Task A_caller_can_assign_a_stable_event_id_that_survives_reopen()
    {
        var conversation = await _store.StartAsync("local");
        var requestId = Guid.NewGuid();

        var appended = await _store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart,
            requestId.ToString("D"), cancellationToken: default, eventId: requestId);

        Assert.Equal(requestId, appended.Id);
        await using var reopened = new ServiceCollection()
            .AddConciergeChat(_databasePath)
            .BuildServiceProvider();
        var persisted = Assert.Single(await reopened.GetRequiredService<IConversationStore>()
            .ReadEventsAsync(conversation.Id));
        Assert.Equal(requestId, persisted.Id);
    }

    [Fact]
    public async Task Sequence_numbers_have_no_gaps()
    {
        var conversation = await _store.StartAsync("local");

        for (var i = 0; i < 5; i++)
        {
            await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, $"message {i}");
        }

        var events = await _store.ReadEventsAsync(conversation.Id);
        Assert.Equal([0, 1, 2, 3, 4], events.Select(e => e.Seq));
    }

    [Fact]
    public async Task Two_conversations_number_their_events_independently()
    {
        var first = await _store.StartAsync("local");
        var second = await _store.StartAsync("local");

        await _store.AppendEventAsync(first.Id, ConversationEventType.UserMessage, "in the first");
        var appended = await _store.AppendEventAsync(second.Id, ConversationEventType.UserMessage, "in the second");

        Assert.Equal(0, appended.Seq);
    }

    [Fact]
    public async Task Events_come_back_in_the_order_they_were_written()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "first");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "second");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "third");

        var events = await _store.ReadEventsAsync(conversation.Id);

        Assert.Equal(["first", "second", "third"], events.Select(e => e.Data));
    }

    [Fact]
    public async Task Reading_after_a_cursor_returns_only_newer_events_in_order()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "first");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "second");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "third");

        var events = await _store.ReadEventsAfterAsync(conversation.Id, afterSeq: 0);

        Assert.Equal([1, 2], events.Select(entry => entry.Seq));
        Assert.Equal(["second", "third"], events.Select(entry => entry.Data));
        Assert.Equal(2, await _store.GetLastEventSequenceAsync(conversation.Id));
        Assert.Null(await _store.GetLastEventSequenceAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Reading_a_bounded_page_advances_from_its_last_sequence()
    {
        var conversation = await _store.StartAsync("local");
        for (var index = 0; index < 7; index++)
            await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, $"event-{index}");

        var first = await _store.ReadEventsPageAsync(conversation.Id, afterSeq: -1, pageSize: 3);
        var second = await _store.ReadEventsPageAsync(conversation.Id, afterSeq: first[^1].Seq, pageSize: 3);
        var third = await _store.ReadEventsPageAsync(conversation.Id, afterSeq: second[^1].Seq, pageSize: 3);

        Assert.Equal([0, 1, 2], first.Select(entry => entry.Seq));
        Assert.Equal([3, 4, 5], second.Select(entry => entry.Seq));
        Assert.Collection(third, entry => Assert.Equal(6, entry.Seq));
    }

    // ── What the rows now distinguish ──────────────────────────────────

    [Fact]
    public async Task A_tool_result_is_not_a_user_message()
    {
        var conversation = await _store.StartAsync("local");

        await _store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult, """{"name":"read_file"}""");

        var recorded = Assert.Single(await _store.ReadEventsAsync(conversation.Id));
        Assert.Equal(ConversationEventType.ToolResult, recorded.Type);
    }

    [Fact]
    public async Task A_tool_call_and_its_result_are_separate_events()
    {
        var conversation = await _store.StartAsync("local");

        await _store.AppendEventAsync(conversation.Id, ConversationEventType.ToolCall, """{"name":"read_file"}""");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult, """{"output":"content"}""");

        var events = await _store.ReadEventsAsync(conversation.Id);
        Assert.Equal(
            [ConversationEventType.ToolCall, ConversationEventType.ToolResult],
            events.Select(e => e.Type));
    }

    [Fact]
    public async Task An_approval_decision_is_recorded_in_the_conversation_itself()
    {
        var conversation = await _store.StartAsync("local");

        await _store.AppendEventAsync(conversation.Id, ConversationEventType.ApprovalDecided, """{"decision":"Denied"}""");

        Assert.Single(await _store.ReadEventsAsync(conversation.Id));
    }

    // ── Derivation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Derived_history_contains_what_was_said()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "a question");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "an answer");

        var turns = await _store.DeriveMessagesAsync(conversation.Id);

        Assert.Equal(["a question", "an answer"], turns.Select(turn => turn.Content));
    }

    [Fact]
    public async Task Derived_history_gives_each_turn_the_right_role()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "a question");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "an answer");

        var turns = await _store.DeriveMessagesAsync(conversation.Id);

        Assert.Equal(["user", "assistant"], turns.Select(turn => turn.Role));
    }

    [Fact]
    public async Task Connection_status_is_durable_but_never_becomes_a_message_or_model_input()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "continue the saved work");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.ConnectionStatus,
            """{"state":"unreachable","peer":"desktop"}""");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "Here is the result.", "Circle AI");

        var saved = await _store.GetAsync(conversation.Id);
        var derived = await _store.DeriveMessagesAsync(conversation.Id);
        var replay = new ConversationReplay(await _store.ReadEventsAsync(conversation.Id)).Steps().Last();

        Assert.Equal(["continue the saved work", "Here is the result."], saved!.Messages.Select(message => message.Content));
        Assert.Equal(["continue the saved work", "Here is the result."], derived.Select(turn => turn.Content));
        Assert.Equal(["continue the saved work", "Here is the result."], replay.HistorySoFar.Select(turn => turn.Content));
        Assert.Contains(await _store.ReadEventsAsync(conversation.Id), entry => entry.Type == ConversationEventType.ConnectionStatus);
    }

    [Fact]
    public async Task Legacy_connection_error_assistant_events_are_hidden_from_history_after_upgrade()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "multiply 12 by 13");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage,
            "Could not reach your Desktop: Socket closed", "Connection status");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "156", "Circle AI on Desktop");

        Assert.Equal(["multiply 12 by 13", "156"], (await _store.GetAsync(conversation.Id))!.Messages.Select(message => message.Content));
        Assert.Equal(["multiply 12 by 13", "156"], (await _store.DeriveMessagesAsync(conversation.Id)).Select(turn => turn.Content));
        Assert.Equal(["multiply 12 by 13", "156"], new ConversationReplay(await _store.ReadEventsAsync(conversation.Id))
            .Steps().Last().HistorySoFar.Select(turn => turn.Content));

        await _store.RebuildProjectionAsync(conversation.Id);
        Assert.Equal(["multiply 12 by 13", "156"], (await _store.GetAsync(conversation.Id))!.Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task Turn_boundaries_do_not_appear_in_derived_history()
    {
        // Bookkeeping events are in the log for replay, but the model must not be shown them.
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.TurnStart, "1");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "a question");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.TurnEnd, "1");

        var turn = Assert.Single(await _store.DeriveMessagesAsync(conversation.Id));

        Assert.Equal("a question", turn.Content);
    }

    [Fact]
    public async Task A_tool_result_reaches_the_model_as_a_tool_result_not_as_the_user()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "read the file");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult, "the file content");

        var turns = await _store.DeriveMessagesAsync(conversation.Id);

        Assert.Equal("tool", turns[^1].Role);
    }

    [Fact]
    public async Task The_system_prompt_leads_the_derived_history()
    {
        var conversation = await _store.StartAsync("local", systemPrompt: "You are Bell.");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "hello");

        var turns = await _store.DeriveMessagesAsync(conversation.Id);

        Assert.Equal("system", turns[0].Role);
        Assert.Contains("Bell", turns[0].Content, StringComparison.Ordinal);
    }

    // ── The log is the source, the rows are a cache ────────────────────

    [Fact]
    public async Task History_can_be_rebuilt_from_the_log_alone()
    {
        // The point of the whole change: delete the projected rows and the conversation is
        // still there, because the log is what is true.
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "a question");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "an answer");
        var before = await _store.DeriveMessagesAsync(conversation.Id);

        await _store.RebuildProjectionAsync(conversation.Id);
        var after = await _store.DeriveMessagesAsync(conversation.Id);

        Assert.Equal(before.Select(t => t.Content), after.Select(t => t.Content));
    }

    [Fact]
    public async Task Rebuilding_restores_the_messages_a_reader_sees()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "a question");

        await _store.RebuildProjectionAsync(conversation.Id);

        Assert.Single((await _store.GetAsync(conversation.Id))!.Messages);
    }

    [Fact]
    public async Task The_log_survives_a_restart()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "durable");

        await using var reopened = new ServiceCollection()
            .AddConciergeChat(_databasePath)
            .BuildServiceProvider();

        var events = await reopened.GetRequiredService<IConversationStore>().ReadEventsAsync(conversation.Id);
        Assert.Single(events);
    }

    // ── Appending stays honest ─────────────────────────────────────────

    [Fact]
    public async Task Appending_to_a_conversation_that_does_not_exist_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.AppendEventAsync(Guid.NewGuid(), ConversationEventType.UserMessage, "orphan"));
    }

    [Fact]
    public async Task Reading_a_conversation_that_does_not_exist_gives_nothing()
    {
        Assert.Empty(await _store.ReadEventsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Appending_from_several_places_at_once_still_produces_contiguous_sequence()
    {
        // Two hosts share one SQLite file, and the MAUI host runs the UI and background work
        // in the same process. A gap or a duplicate here corrupts every later derivation.
        var conversation = await _store.StartAsync("local");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, $"message {i}")));

        var events = await _store.ReadEventsAsync(conversation.Id);
        Assert.Equal(Enumerable.Range(0, 20), events.Select(e => e.Seq));
    }
}
