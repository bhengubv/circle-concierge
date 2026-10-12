using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json;
using Concierge.Away;
using Concierge.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class WristInboxTests
{
    [Fact]
    public async Task Slow_desktop_does_not_leave_watch_history_waiting_for_minutes()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/away/conversations", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            return Array.Empty<WristConversation>();
        });
        await app.StartAsync();
        var client = new AwayClient(hostConfigPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing"));
        client.PointAt(app.Urls.Single());
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await client.ReadConversationsAsync());
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 2, 6);
        await app.StopAsync();
    }
    [Fact]
    public void Read_marker_survives_restart_and_new_replies_become_unread()
    {
        var path = Path.Combine(Path.GetTempPath(), "wrist-read-" + Guid.NewGuid(), "inbox.json");
        try
        {
            var thread = new WristConversation(Guid.NewGuid(), "Shopping", DateTimeOffset.UtcNow, "Answered", 4);
            var inbox = new WristInbox(path); inbox.Save([thread]); Assert.True(inbox.IsUnread(thread));
            inbox.MarkRead(thread.Id, 4);
            Assert.False(new WristInbox(path).IsUnread(thread));
            Assert.True(new WristInbox(path).IsUnread(thread with { LastMessageSeq = 6 }));
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public async Task Voice_continues_the_thread_chosen_on_the_watch()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        Guid? received = null;
        app.MapPost("/api/away/say", async (Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            using var json = await JsonDocument.ParseAsync(request.Body);
            received = json.RootElement.GetProperty("conversationId").GetGuid();
            return Microsoft.AspNetCore.Http.Results.Json(new AwayReply(true, null, "Added milk to our conversation", received));
        });
        app.MapGet("/api/away/waiting", () => Array.Empty<AwayWaiting>());
        app.MapGet("/api/away/change", () => Microsoft.AspNetCore.Http.Results.NoContent());
        await app.StartAsync();
        var path = Path.Combine(Path.GetTempPath(), "wrist-route-" + Guid.NewGuid());
        try
        {
            var client = new AwayClient(new AwayOutbox(Path.Combine(path, "outbox")), hostConfigPath: Path.Combine(path, "host.txt"));
            client.PointAt(app.Urls.Single());
            var hands = new WatchHands(client) { ConversationId = Guid.NewGuid() };
            var answer = await hands.SaidAsync("Add milk", new(DateTimeOffset.UtcNow));
            Assert.Equal(hands.ConversationId, received);
            Assert.Equal("Added milk to our conversation", answer.Say);
            Assert.Equal(0, client.Waiting);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); await app.StopAsync(); }
    }
    [Theory]
    [InlineData("completed", "Answered")]
    [InlineData("failed", "Needs retry")]
    [InlineData("cancelled", "Stopped")]
    public void Latest_receipt_wins_over_old_interrupted_runs(string status, string expected)
    {
        var events = new[] { Entry(0, ConversationEventType.TurnStart, "{}"), Entry(1, ConversationEventType.TurnStart, "{}"),
            Entry(2, ConversationEventType.TurnEnd, JsonSerializer.Serialize(new TurnRunReceipt(Guid.NewGuid(), status, "Answer", "test", [], 0, null))) };
        Assert.Equal(expected, WristTimeline.Status(events, false));
        Assert.Equal("Working", WristTimeline.Status(events, true));
    }

    [Fact]
    public void Unfinished_run_is_not_falsely_reported_working()
        => Assert.Equal("Interrupted", WristTimeline.Status([Entry(0, ConversationEventType.TurnStart, "{}")], false));

    [Fact]
    public void Recorded_stream_failure_is_not_an_answer()
        => Assert.Equal("Needs retry", WristTimeline.Status([Entry(1, ConversationEventType.TurnEnd,
            JsonSerializer.Serialize(new TurnRunReceipt(Guid.NewGuid(), "completed", "[stream error: 503]", "test", [], 0, null)))], false));

    [Fact]
    public void Transcript_includes_actual_words_and_status_not_tool_payloads()
    {
        var messages = WristTimeline.Messages([Entry(2, ConversationEventType.ConnectionStatus, "Desktop offline"),
            Entry(0, ConversationEventType.UserMessage, "Remember the shopping"), Entry(1, ConversationEventType.ToolCall, "secret-tool-payload")]);
        Assert.Equal(new[] { "Remember the shopping", "Desktop offline" }, messages.Select(m => m.Text));
        Assert.Equal(new[] { "You", "Status" }, messages.Select(m => m.Who));
    }

    [Fact]
    public void History_survives_watch_process_restart_and_is_bounded()
    {
        var path = Path.Combine(Path.GetTempPath(), "wrist-test-" + Guid.NewGuid(), "inbox.json");
        try
        {
            var id = Guid.NewGuid(); var inbox = new WristInbox(path);
            inbox.Save([new(id, "From the watch", DateTimeOffset.UtcNow, "Answered")]);
            inbox.Save(id: id, messages: Enumerable.Range(0, 150).Select(i => new WristMessage(i, "You", "Message " + i, DateTimeOffset.UtcNow)).ToArray());
            var reopened = new WristInbox(path);
            Assert.Equal("From the watch", Assert.Single(reopened.Snapshot.Conversations).Title);
            Assert.Equal(100, reopened.Snapshot.Threads[id].Count);
            Assert.Equal("Message 50", reopened.Snapshot.Threads[id][0].Text);
            inbox.Save([]);
            Assert.Empty(new WristInbox(path).Snapshot.Threads);
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
    }

    [Fact]
    public async Task Desktop_projection_reads_real_sqlite_history_and_current_run_state()
    {
        var path = Path.Combine(Path.GetTempPath(), "wrist-db-" + Guid.NewGuid() + ".db");
        await using var provider = new ServiceCollection().AddConciergeChat(path).BuildServiceProvider();
        var store = provider.GetRequiredService<IConversationStore>();
        var thread = await store.StartAsync("local", "Shopping list");
        await store.StartAsync("other-person", "Private thread");
        for (int i = 0; i < 110; i++) await store.AppendEventAsync(thread.Id, ConversationEventType.UserMessage, "Item " + i);
        var recent = await store.ReadRecentEventsAsync(thread.Id);
        Assert.Equal(100, recent.Count); Assert.Equal(10, recent[0].Seq); Assert.Equal(109, recent[^1].Seq);
        var runs = new BackgroundRuns(); using var stop = new CancellationTokenSource(); runs.Started(thread.Id, stop);
        var projection = await WristReadModel.ReadAsync(store, runs, default);
        Assert.Equal(thread.Id, Assert.Single(projection).Id); Assert.Equal("Working", projection[0].Status);
        runs.Finished(thread.Id);
        Assert.Equal("Saved", (await WristReadModel.ReadAsync(store, runs, default))[0].Status);
        // SQLite owns the file until the provider is disposed; disposable test data lives in TEMP.
    }

    private static ConversationEvent Entry(int seq, ConversationEventType type, string data)
        => new() { Seq = seq, Type = type, Data = data };
}
