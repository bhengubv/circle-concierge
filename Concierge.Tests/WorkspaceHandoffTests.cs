using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The workspace owns "/" and "/chat", and everything arriving by query string —
/// ?q=, ?voice=1, ?image=1, ?demo=1 — has to be consumed when the component sees it.
///
/// This exists because that broke and nothing noticed. Putting @page "/" on the
/// workspace made "/" and "/chat" the same component, so moving between them is
/// not a new component: OnInitializedAsync does not run again, and — verified on
/// the running app — OnParametersSetAsync did not either. Every handoff was
/// dropped in silence, and finding out cost a build, a launch, a click and a
/// database query. These run in about two seconds.
///
/// Two things about the shape of these tests, both learned the hard way:
///
///   * They are synchronous. bUnit dispatches component work through its own
///     renderer, and an `async Task` test yields to a context that never pumps
///     it — the assertions simply never observed the handoff, which looked
///     exactly like the bug they were written to catch.
///
///   * Messages are read with GetAsync, not from what ListAsync returns.
///     ListAsync feeds the sidebar and carries titles and times but no turns,
///     so asserting on its Messages fails while the handoff is working
///     perfectly — which cost an hour of chasing the wrong thing.
/// </summary>
public sealed class WorkspaceHandoffTests : BunitContext
{
    [Fact]
    public void A_prompt_handed_over_by_query_string_starts_a_conversation()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"handoff-{Guid.NewGuid():N}.db");
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

        var nav = Services.GetRequiredService<NavigationManager>();

        // Land on the workspace, exactly as the app does.
        var cut = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();

        // Then navigate to the SAME component with a handoff on the query string.
        // This is the move that used to do nothing at all.
        nav.NavigateTo("/chat?q=Handoff%20regression%20check");

        var store = Services.GetRequiredService<IConversationStore>();

        // Waited for rather than slept through. A fixed 700ms is either longer than
        // it needs to be or shorter than a loaded machine needs, and usually both
        // on different days — this failed about once in a hundred runs and passed
        // on retry, which is the worst way for a test to be wrong. The fifth of
        // this shape in the suite, and the only one that hid behind a sleep.
        // Rendered *inside* the wait rather than once after it, which is what made
        // this flaky. The order used to be: poll, then render, then assert. If the
        // handoff needs a render to fire, the poll could only ever time out, the
        // render then started the work, and the assertion read the store before it
        // had finished — so it passed on a quiet machine and failed on a busy one,
        // which is the worst way for a test to be wrong.
        //
        // And it waits for the **message**, not for the conversation. Waiting for the
        // conversation to exist stopped one step too early: the conversation is created first
        // and the turn appended after it, so on a loaded machine the loop finished, the
        // assertion ran, and `conversation.Messages` was empty. That is the same defect the
        // paragraph above describes, one layer in — waiting for a thing that happens near
        // what you care about rather than for what you care about.
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline && !HasATurn(store))
        {
            cut.Render();
            Thread.Sleep(25);
        }

        var listed = Assert.Single(store.ListAsync("local").GetAwaiter().GetResult());

        // ListAsync returns conversations without their messages — it feeds the
        // sidebar, which only needs titles and times. GetAsync is what loads the
        // turns.
        var conversation = store.GetAsync(listed.Id).GetAwaiter().GetResult();
        Assert.NotNull(conversation);
        var message = Assert.Single(conversation!.Messages);

        Assert.Equal("user", message.Role);
        Assert.Equal("Handoff regression check", message.Content);
    }

    /// <summary>
    /// The same handoff must not fire twice: LocationChanged and the render loop
    /// both re-enter the same method, and a repeat would send the prompt again.
    /// </summary>
    [Fact]
    public void The_same_handoff_is_consumed_once()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"handoff-{Guid.NewGuid():N}.db");
        Services.AddLogging();
        Services.AddConciergeCore();

        // Session state defaults to the real per-user file. A test that reads
        // it depends on whatever the app last did, and a test that writes it
        // changes the user's app state — both happened before this line.
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

        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();

        nav.NavigateTo("/chat?q=Only%20once");
        Thread.Sleep(700);
        cut.Render();

        // Navigating to the identical URL again must change nothing.
        nav.NavigateTo("/chat?q=Only%20once");
        Thread.Sleep(700);
        cut.Render();

        var store = Services.GetRequiredService<IConversationStore>();
        var conversations = store.ListAsync("local").GetAwaiter().GetResult();
        var only = Assert.Single(conversations);

        var conversation = store.GetAsync(only.Id).GetAwaiter().GetResult();
        Assert.NotNull(conversation);
        Assert.Single(conversation!.Messages);
    }

    /// <summary>
    /// Opening the workspace is not the same as being handed a prompt: a plain
    /// navigation with no query string must not start anything.
    /// </summary>
    [Fact]
    public void Navigating_without_a_handoff_starts_nothing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"handoff-{Guid.NewGuid():N}.db");
        Services.AddLogging();
        Services.AddConciergeCore();

        // Session state defaults to the real per-user file. A test that reads
        // it depends on whatever the app last did, and a test that writes it
        // changes the user's app state — both happened before this line.
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

        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();

        nav.NavigateTo("/chat");
        Thread.Sleep(700);
        cut.Render();

        var store = Services.GetRequiredService<IConversationStore>();
        Assert.Empty(store.ListAsync("local").GetAwaiter().GetResult());
    }

    /// <summary>
    /// Whether the handoff has actually landed: a conversation with a turn in it. The whole
    /// point is that a conversation on its own is not the thing being waited for.
    /// </summary>
    private static bool HasATurn(IConversationStore store)
    {
        var listed = store.ListAsync("local").GetAwaiter().GetResult();

        if (listed.Count == 0)
        {
            return false;
        }

        var conversation = store.GetAsync(listed[0].Id).GetAwaiter().GetResult();

        return conversation is { Messages.Count: > 0 };
    }
}
