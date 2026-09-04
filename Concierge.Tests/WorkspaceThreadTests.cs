using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
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
        => Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>(ps => ps.Add(p => p.ConversationId, id));

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
        Assert.Equal(2, cut.FindAll("article.msg .msg-body p.prose").Count);
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
    public void A_chip_is_collapsed_until_it_is_asked()
    {
        var store = Compose();
        var conversation = store.StartAsync("local", title: "With tools").GetAwaiter().GetResult();
        store.AppendEventAsync(conversation.Id, ConversationEventType.ToolResult,
            "Read 6 files\nthe detailed output nobody wants by default").GetAwaiter().GetResult();

        var cut = Open(conversation.Id);

        Assert.Equal("false", cut.Find("button.chip").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("pre.chip-out"));

        cut.Find("button.chip").Click();

        Assert.Equal("true", cut.Find("button.chip").GetAttribute("aria-expanded"));
        Assert.Contains("the detailed output nobody wants by default", cut.Find("pre.chip-out").TextContent);
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

        Assert.NotNull(cut.Find(".ws-thread"));
        Assert.Empty(cut.FindAll(".empty"));
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
