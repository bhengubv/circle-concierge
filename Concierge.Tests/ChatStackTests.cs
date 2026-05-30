using Concierge.Ai;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ChatStackTests
{
    [Fact]
    public void Add_concierge_chat_registers_store_and_default_runtime()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"concierge-chat-{Guid.NewGuid():N}.db");
        try
        {
            using var provider = new ServiceCollection()
                .AddConciergeChat(dbPath)
                .BuildServiceProvider();

            Assert.NotNull(provider.GetService<IConversationStore>());
            Assert.NotNull(provider.GetService<IDbContextFactory<ConciergeChatDbContext>>());
            var runtime = provider.GetService<IChatRuntime>();
            Assert.NotNull(runtime);
            Assert.IsType<NullChatRuntime>(runtime);
            Assert.False(runtime!.IsReady);
            Assert.Equal("No engine wired", runtime.EngineLabel);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Conversation_store_round_trips_messages_and_auto_titles_from_first_user_turn()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"concierge-chat-{Guid.NewGuid():N}.db");
        try
        {
            using var provider = new ServiceCollection()
                .AddConciergeChat(dbPath)
                .BuildServiceProvider();
            await StartSchemaAsync(provider);
            var store = provider.GetRequiredService<IConversationStore>();

            var conversation = await store.StartAsync("local");
            Assert.Equal("New conversation", conversation.Title);

            await store.AppendAsync(conversation.Id, "user", "Hello there. How do you handle approvals?");
            await store.AppendAsync(conversation.Id, "assistant", "Approvals route through the harness.", producedBy: "TestEngine");

            var loaded = await store.GetAsync(conversation.Id);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Messages.Count);
            Assert.Equal("user", loaded.Messages[0].Role);
            Assert.Equal("assistant", loaded.Messages[1].Role);
            Assert.Equal("TestEngine", loaded.Messages[1].ProducedBy);
            Assert.StartsWith("Hello there.", loaded.Title);

            var list = await store.ListAsync("local");
            Assert.Single(list);

            await store.DeleteAsync(conversation.Id);
            Assert.Empty(await store.ListAsync("local"));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Null_chat_runtime_yields_status_message_when_streamed()
    {
        IChatRuntime runtime = new NullChatRuntime();
        var chunks = new List<string>();
        await foreach (var chunk in runtime.StreamAsync([new ChatTurn("user", "hi")]))
        {
            chunks.Add(chunk);
        }

        Assert.False(runtime.IsReady);
        Assert.Single(chunks);
        Assert.Contains("engine", chunks[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_concierge_ai_replaces_default_chat_runtime_with_circleai_backed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"concierge-chat-{Guid.NewGuid():N}.db");
        try
        {
            // Point the loader at an isolated models dir + skip the downloader so the runtime
            // surfaces its "engine offline" status instead of trying to fetch a model under test.
            var options = new CircleAiChatOptions
            {
                ModelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-models-{Guid.NewGuid():N}"),
                RepositoryUrl = null,
            };

            await using var provider = new ServiceCollection()
                .AddLogging()
                .AddConciergeCore()
                .AddConciergeChat(dbPath)
                .AddConciergeAi(options)
                .BuildServiceProvider();

            var runtime = provider.GetRequiredService<IChatRuntime>();
            Assert.IsType<CircleAiChatRuntime>(runtime);
            Assert.Equal("CircleAI (pending)", runtime.EngineLabel);
            Assert.False(runtime.IsReady);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Circleai_chat_runtime_surfaces_engine_offline_when_model_cannot_be_resolved()
    {
        var options = new CircleAiChatOptions
        {
            ModelsDirectory = Path.Combine(Path.GetTempPath(), $"concierge-models-{Guid.NewGuid():N}"),
            RepositoryUrl = null,
        };
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<CircleAiChatRuntime>.Instance;
        var runtime = new CircleAiChatRuntime(logger, options);

        await runtime.LoadAsync(CancellationToken.None);

        Assert.False(runtime.IsReady);
        Assert.Contains("offline", runtime.StatusMessage, StringComparison.OrdinalIgnoreCase);

        var chunks = new List<string>();
        await foreach (var chunk in runtime.StreamAsync([new ChatTurn("user", "ping")]))
        {
            chunks.Add(chunk);
        }
        Assert.Single(chunks);
        Assert.Contains("offline", chunks[0], StringComparison.OrdinalIgnoreCase);

        await runtime.DisposeAsync();
    }

    [Fact]
    public void Chat_options_default_to_a_writable_local_app_data_path()
    {
        var options = new CircleAiChatOptions();
        Assert.False(string.IsNullOrWhiteSpace(options.ModelsDirectory));
        Assert.True(options.ContextSize > 0);
        Assert.NotNull(options.RepositoryUrl);
    }

    private static async Task StartSchemaAsync(IServiceProvider provider)
    {
        // The schema initializer is registered as IHostedService; in unit tests no host runs
        // so we call it directly to materialize the SQLite file before the store reads from it.
        var factory = provider.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Test cleanup is best-effort.
        }
    }
}
