using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// What a conversation must be able to do beyond being read (parity features 25, 28, 29):
/// branch, be searched, and leave the device.
/// </summary>
/// <remarks>
/// Forking matters most on a small model: when a conversation goes wrong, branching from the
/// last good point is cheaper than starting over, because everything before it stays in the
/// warmed cache. Export matters because the data is the user's — a local-first product that
/// cannot hand back what it holds is only local, not the user's.
/// </remarks>
public sealed class ConversationLifecycleTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"concierge-lifecycle-{Guid.NewGuid():N}.db");

    private ServiceProvider _provider = null!;
    private IConversationStore _store = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddConciergeChat(_databasePath)
            .BuildServiceProvider();

        // No host runs in a unit test, so the schema initializer never fires; create it here.
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
            // The file is disposable; a held handle is not worth failing a test over.
        }
    }

    // ── Forking ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_fork_keeps_the_messages_up_to_the_branch_point()
    {
        var original = await SeedConversation("one", "two", "three");
        var messages = (await _store.GetAsync(original.Id))!.Messages;

        var fork = await _store.ForkAsync(original.Id, messages[1].Id);

        var forked = (await _store.GetAsync(fork.Id))!;
        Assert.Equal(["one", "two"], forked.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task A_fork_leaves_the_original_untouched()
    {
        var original = await SeedConversation("one", "two", "three");
        var messages = (await _store.GetAsync(original.Id))!.Messages;

        await _store.ForkAsync(original.Id, messages[0].Id);

        Assert.Equal(3, (await _store.GetAsync(original.Id))!.Messages.Count);
    }

    [Fact]
    public async Task A_fork_is_a_conversation_of_its_own()
    {
        var original = await SeedConversation("one", "two");
        var messages = (await _store.GetAsync(original.Id))!.Messages;

        var fork = await _store.ForkAsync(original.Id, messages[0].Id);

        Assert.NotEqual(original.Id, fork.Id);
        Assert.Contains(await _store.ListAsync("local"), c => c.Id == fork.Id);
    }

    [Fact]
    public async Task A_fork_keeps_the_system_prompt()
    {
        var original = await _store.StartAsync("local", "Titled", "You are Bell.");
        await _store.AppendAsync(original.Id, "user", "hello");
        var messages = (await _store.GetAsync(original.Id))!.Messages;

        var fork = await _store.ForkAsync(original.Id, messages[0].Id);

        Assert.Equal("You are Bell.", fork.SystemPrompt);
    }

    [Fact]
    public async Task Forking_from_a_message_that_is_not_there_is_refused()
    {
        var original = await SeedConversation("one");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.ForkAsync(original.Id, Guid.NewGuid()));
    }

    // ── Searching ──────────────────────────────────────────────────────

    [Fact]
    public async Task Search_finds_a_conversation_by_something_said_in_it()
    {
        await SeedConversation("the quick brown fox");
        await SeedConversation("something else entirely");

        var hits = await _store.SearchAsync("local", "brown fox");

        Assert.Single(hits);
    }

    [Fact]
    public async Task Search_finds_a_conversation_by_its_title()
    {
        await _store.StartAsync("local", "Homework help");

        Assert.Single(await _store.SearchAsync("local", "homework"));
    }

    [Fact]
    public async Task Search_ignores_capitalisation()
    {
        await SeedConversation("The Quick Brown Fox");

        Assert.Single(await _store.SearchAsync("local", "QUICK"));
    }

    [Fact]
    public async Task Search_does_not_reach_into_another_owner_s_conversations()
    {
        await _store.StartAsync("someone-else", "their private thing");

        Assert.Empty(await _store.SearchAsync("local", "private"));
    }

    [Fact]
    public async Task An_empty_search_returns_nothing_rather_than_everything()
    {
        await SeedConversation("anything at all");

        Assert.Empty(await _store.SearchAsync("local", "   "));
    }

    // ── Exporting ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_export_contains_what_was_said()
    {
        var conversation = await SeedConversation("hello there", "general kenobi");

        var exported = await _store.ExportAsync(conversation.Id);

        Assert.Contains("hello there", exported, StringComparison.Ordinal);
        Assert.Contains("general kenobi", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_says_who_said_what()
    {
        var conversation = await _store.StartAsync("local");
        await _store.AppendAsync(conversation.Id, "user", "a question");
        await _store.AppendAsync(conversation.Id, "assistant", "an answer");

        var exported = await _store.ExportAsync(conversation.Id);

        Assert.Contains("user", exported, StringComparison.Ordinal);
        Assert.Contains("assistant", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporting_a_conversation_that_is_not_there_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ExportAsync(Guid.NewGuid()));
    }

    private async Task<Conversation> SeedConversation(params string[] messages)
    {
        var conversation = await _store.StartAsync("local");
        foreach (var message in messages)
        {
            await _store.AppendAsync(conversation.Id, "user", message);
        }

        return conversation;
    }
}
