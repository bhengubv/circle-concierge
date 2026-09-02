using System.Runtime.CompilerServices;
using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// What resume must do (parity feature 26): bring a conversation back after the process
/// died, with the agent's working state and not only the visible messages.
/// </summary>
/// <remarks>
/// Android kills apps. <c>IPersistableChatRuntime</c> already snapshots the KV cache on
/// sleep, but nothing put the conversation back together on the other side — reopening gave
/// you the text and a cold engine. Resume is the missing half.
/// </remarks>
public sealed class ConversationResumeTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"concierge-resume-{Guid.NewGuid():N}.db");

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

    [Fact]
    public async Task Resuming_restores_what_was_said()
    {
        var conversation = await SeedAsync();
        var resumer = new ConversationResumer(_store, new SnapshottingRuntime());

        var resumed = await resumer.ResumeAsync(conversation.Id);

        Assert.Equal(["a question", "an answer"], resumed.History.Select(turn => turn.Content));
    }

    [Fact]
    public async Task Resuming_asks_the_engine_to_restore_its_state()
    {
        var conversation = await SeedAsync();
        var runtime = new SnapshottingRuntime();

        await new ConversationResumer(_store, runtime).ResumeAsync(conversation.Id);

        Assert.True(runtime.LoadAttempted);
    }

    [Fact]
    public async Task Resuming_reports_that_the_engine_state_came_back()
    {
        var conversation = await SeedAsync();
        var runtime = new SnapshottingRuntime { LoadSucceeds = true };

        var resumed = await new ConversationResumer(_store, runtime).ResumeAsync(conversation.Id);

        Assert.True(resumed.EngineStateRestored);
    }

    [Fact]
    public async Task A_lost_snapshot_still_resumes_the_conversation()
    {
        // The snapshot is an optimisation. Losing it costs the prefill, not the conversation.
        var conversation = await SeedAsync();
        var runtime = new SnapshottingRuntime { LoadSucceeds = false };

        var resumed = await new ConversationResumer(_store, runtime).ResumeAsync(conversation.Id);

        Assert.False(resumed.EngineStateRestored);
        Assert.Equal(2, resumed.History.Count);
    }

    [Fact]
    public async Task An_engine_that_cannot_snapshot_still_resumes_the_conversation()
    {
        // Cloud runtimes hold no in-process state, so they do not implement the interface.
        var conversation = await SeedAsync();

        var resumed = await new ConversationResumer(_store, new PlainRuntime()).ResumeAsync(conversation.Id);

        Assert.Equal(2, resumed.History.Count);
        Assert.False(resumed.EngineStateRestored);
    }

    [Fact]
    public async Task A_conversation_that_does_not_exist_cannot_be_resumed()
    {
        var resumer = new ConversationResumer(_store, new SnapshottingRuntime());

        await Assert.ThrowsAsync<InvalidOperationException>(() => resumer.ResumeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_damaged_log_is_reported_rather_than_resumed_silently()
    {
        // A log that arrived incomplete over the mesh must not become a conversation that
        // looks whole. The user is told, and the history that is there is still returned.
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult, "orphan result");

        var resumed = await new ConversationResumer(_store, new SnapshottingRuntime()).ResumeAsync(conversation.Id);

        Assert.NotEmpty(resumed.Problems);
    }

    [Fact]
    public async Task A_sound_log_resumes_with_nothing_to_report()
    {
        var conversation = await SeedAsync();

        var resumed = await new ConversationResumer(_store, new SnapshottingRuntime()).ResumeAsync(conversation.Id);

        Assert.Empty(resumed.Problems);
    }

    private async Task<Conversation> SeedAsync()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.UserMessage, "a question");
        await _store.AppendEventAsync(conversation.Id, ConversationEventType.AssistantMessage, "an answer");
        return conversation;
    }

    private sealed class SnapshottingRuntime : IChatRuntime, IPersistableChatRuntime
    {
        public bool LoadAttempted { get; private set; }
        public bool LoadSucceeds { get; set; } = true;

        public string Id => "snapshotting";
        public string EngineLabel => "Snapshotting";
        public bool IsReady => true;
        public string StatusMessage => "ready";
        public string? SessionSnapshotPath => Path.Combine(Path.GetTempPath(), "concierge-test-snapshot.bin");

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return "answer";
        }

        public Task<bool> SaveSessionAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> LoadSessionAsync(string path, CancellationToken cancellationToken = default)
        {
            LoadAttempted = true;
            return Task.FromResult(LoadSucceeds);
        }
    }

    private sealed class PlainRuntime : IChatRuntime
    {
        public string Id => "plain";
        public string EngineLabel => "Plain";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return "answer";
        }
    }
}
