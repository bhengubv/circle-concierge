using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The workspace sidebar: threads, approvals, skills and what is answering.
///
/// This replaced nineteen routes, a ⋯ menu and a bottom tab bar, and every claim
/// made about it so far was checked by photographing the screen. These pin the
/// behaviour instead — including the two design rules the sidebar exists to
/// honour: long lists fold rather than scroll, and a folded group still tells
/// you how much is in it.
///
/// Synchronous by design, and messages are read with GetAsync rather than from
/// ListAsync — see WorkspaceHandoffTests for why both of those matter.
/// </summary>
public sealed class WorkspaceSidebarTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> RenderWorkspace()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sidebar-{Guid.NewGuid():N}.db");
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
        // Settings opens from this sidebar, and Settings reads the safety audit log.
        Concierge.Shared.Safety.SafetyServiceCollectionExtensions.AddConciergeSafety(Services);
        Services.AddSingleton<IEnumerable<Concierge.Shared.Media.IImageRuntime>>(
            _ => Array.Empty<Concierge.Shared.Media.IImageRuntime>());
        JSInterop.Mode = JSRuntimeMode.Loose;

        using (var db = Services.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>().CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        return Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();
    }

    private static IElement Header(IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut, string name)
        => cut.FindAll("button.ws-head")
              .First(b => b.QuerySelector(".ws-head-name")!.TextContent.Trim() == name);

    // ── What is open when you arrive ──────────────────────────────────────

    /// <summary>
    /// One group at a time, and it is Threads — the work.
    ///
    /// Threads and Approvals both opened until Rooms made a fourth group: four
    /// headers with two bodies unfolded overflowed the sidebar by 67px in a
    /// 479px-tall window, putting Rooms below the fold and Skills behind the
    /// runtime footer, with no scrollbar to say so. Capping the thread list
    /// recovered 3px, because the "show all" control replaces the row it
    /// hides. An accordion cannot outgrow the space it has.
    ///
    /// This is measured, and bUnit cannot measure it — see the fit note in
    /// RoomTests. What is asserted here is the structure that follows from it.
    /// </summary>
    [Fact]
    public void Only_one_group_is_open_and_it_is_the_work()
    {
        var cut = RenderWorkspace();

        var groups = cut.FindAll("section.ws-group");
        Assert.Equal(5, groups.Count);

        Assert.Contains("is-open", groups[0].ClassName);        // Threads
        Assert.DoesNotContain("is-open", groups[1].ClassName);  // Approvals
        Assert.DoesNotContain("is-open", groups[2].ClassName);  // Skills
        Assert.DoesNotContain("is-open", groups[3].ClassName);  // Rooms
        Assert.DoesNotContain("is-open", groups[4].ClassName);  // Tools
    }

    /// <summary>
    /// Opening one closes the last. Without this the sidebar grows back to the
    /// size that did not fit.
    /// </summary>
    [Fact]
    public void Opening_a_group_folds_the_one_that_was_open()
    {
        var cut = RenderWorkspace();

        Header(cut, "Rooms").Click();

        var groups = cut.FindAll("section.ws-group");
        Assert.DoesNotContain("is-open", groups[0].ClassName);  // Threads folded
        Assert.Contains("is-open", groups[3].ClassName);        // Rooms open
    }

    /// <summary>
    /// The order is the conversation first and the destinations last: what you
    /// are doing, what is waiting on you, what is shaping the answers, then the
    /// rooms you go and read, and last the tools you go and use.
    /// </summary>
    [Fact]
    public void The_groups_run_from_the_conversation_to_the_destinations()
    {
        var cut = RenderWorkspace();

        var names = cut.FindAll(".ws-head-name").Select(e => e.TextContent.Trim()).ToArray();
        Assert.Equal(new[] { "Threads", "Approvals", "Skills", "Rooms", "Tools" }, names);
    }

    /// <summary>
    /// Everything the workspace can reach, reachable. History, Diagrams and
    /// Images kept their routes through the redesign and would otherwise be
    /// addressable only by typing a URL.
    /// </summary>
    [Fact]
    public void Every_tool_is_reachable_from_the_sidebar()
    {
        var cut = RenderWorkspace();
        Header(cut, "Tools").Click();

        var hrefs = cut.FindAll("section.ws-group")[4]
            .QuerySelectorAll("a.ws-room")
            .Select(a => a.GetAttribute("href"))
            .ToArray();

        Assert.Equal(new[] { "history", "diagrams", "images" }, hrefs);
    }

    /// <summary>
    /// Every room is reachable. The rooms kept their routes through the
    /// redesign, and with the tab bar and the ⋯ menu both gone this group is
    /// the only thing that reaches them — a route nothing links to is deleted
    /// in every way that matters.
    /// </summary>
    [Fact]
    public void Every_room_is_reachable_from_the_sidebar()
    {
        var cut = RenderWorkspace();
        Header(cut, "Rooms").Click();

        var hrefs = cut.FindAll("a.ws-room").Select(a => a.GetAttribute("href")).ToArray();

        Assert.Equal(
            new[] { "product", "engineering", "beyond", "business-apis", "roadmap", "release", "pricing" },
            hrefs);
    }

    // ── Folding ───────────────────────────────────────────────────────────

    [Fact]
    public void A_group_folds_and_unfolds_when_its_header_is_clicked()
    {
        var cut = RenderWorkspace();

        Header(cut, "Skills").Click();
        Assert.Contains("is-open", cut.FindAll("section.ws-group")[2].ClassName);

        Header(cut, "Skills").Click();
        Assert.DoesNotContain("is-open", cut.FindAll("section.ws-group")[2].ClassName);
    }

    /// <summary>
    /// The whole reason folding is acceptable: a closed group still says how much
    /// is inside it, so a folded Approvals still reports what is waiting on you.
    /// That matters more now that Approvals arrives folded — the count is the
    /// only thing telling you two decisions are outstanding.
    /// </summary>
    [Fact]
    public void A_folded_group_still_shows_its_count()
    {
        var cut = RenderWorkspace();

        var group = cut.FindAll("section.ws-group")[1];
        Assert.DoesNotContain("is-open", group.ClassName);

        var count = group.QuerySelector(".ws-head-count")!.TextContent.Trim();
        Assert.Equal("2", count);
    }

    [Fact]
    public void A_folded_group_renders_none_of_its_items()
    {
        var cut = RenderWorkspace();

        // Skills is closed on arrival, so none of its sixty-nine rows exist.
        var skills = cut.FindAll("section.ws-group")[2];
        Assert.Empty(skills.QuerySelectorAll(".ws-item"));

        Header(cut, "Skills").Click();
        Assert.NotEmpty(cut.FindAll("section.ws-group")[2].QuerySelectorAll(".ws-item"));
    }

    // ── Approvals ─────────────────────────────────────────────────────────

    /// <summary>
    /// Risk is a dot and a word — never a filled card, a coloured badge or a
    /// tinted edge. That rule is the whole status vocabulary of this app, and it
    /// is easy to undo by accident.
    /// </summary>
    [Fact]
    public void An_approval_shows_its_risk_as_a_dot_and_nothing_louder()
    {
        var cut = RenderWorkspace();
        Header(cut, "Approvals").Click();

        var approvals = cut.FindAll("section.ws-group")[1];
        var rows = approvals.QuerySelectorAll(".ws-item");
        Assert.Equal(2, rows.Length);

        // Every row carries exactly one dot.
        foreach (var row in rows)
        {
            Assert.Single(row.QuerySelectorAll(".dot"));
        }

        // High risk is the danger dot; medium is the waiting dot.
        var classes = rows.SelectMany(r => r.QuerySelectorAll(".dot")).Select(d => d.ClassName!).ToArray();
        Assert.Contains(classes, c => c.Contains("dot-danger"));
        Assert.Contains(classes, c => c.Contains("dot-waiting"));

        // And nothing in the sidebar is a pill, a badge or a filled card.
        Assert.Empty(cut.FindAll(".ws-side .pill"));
    }

    [Fact]
    public void Approvals_are_named_so_you_can_tell_them_apart()
    {
        var cut = RenderWorkspace();
        Header(cut, "Approvals").Click();

        var titles = cut.FindAll("section.ws-group")[1]
            .QuerySelectorAll(".ws-item-label")
            .Select(e => e.TextContent.Trim())
            .ToArray();

        Assert.Contains("MCP filesystem tool", titles);
        Assert.Contains("Review workspace write", titles);
    }

    // ── Threads ───────────────────────────────────────────────────────────

    [Fact]
    public void With_no_conversations_the_threads_group_offers_a_new_one()
    {
        var cut = RenderWorkspace();

        var threads = cut.FindAll("section.ws-group")[0];
        var labels = threads.QuerySelectorAll(".ws-item-label").Select(e => e.TextContent.Trim()).ToArray();

        Assert.Equal(new[] { "New thread" }, labels);
    }

    [Fact]
    public void A_conversation_appears_as_a_link_to_itself()
    {
        // Seeded BEFORE the component renders. The sidebar loads its list once,
        // in OnInitializedAsync, and re-rendering does not re-read the store —
        // so a conversation created afterwards will not appear until something
        // calls RefreshSidebarAsync. Worth knowing, and not what this test is
        // about.
        var dbPath = Path.Combine(Path.GetTempPath(), $"sidebar-{Guid.NewGuid():N}.db");
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
        // Settings opens from this sidebar, and Settings reads the safety audit log.
        Concierge.Shared.Safety.SafetyServiceCollectionExtensions.AddConciergeSafety(Services);
        Services.AddSingleton<IEnumerable<Concierge.Shared.Media.IImageRuntime>>(
            _ => Array.Empty<Concierge.Shared.Media.IImageRuntime>());
        JSInterop.Mode = JSRuntimeMode.Loose;
        using (var db = Services.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>().CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        var store = Services.GetRequiredService<IConversationStore>();
        var started = store.StartAsync("local", title: "Notes tidy-up").GetAwaiter().GetResult();

        var cut = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();

        var link = cut.FindAll("section.ws-group")[0]
            .QuerySelectorAll("a.ws-item")
            .Single(a => a.TextContent.Contains("Notes tidy-up"));

        Assert.Equal($"/chat/{started.Id}", link.GetAttribute("href"));
    }

    // ── What is answering ─────────────────────────────────────────────────

    /// <summary>
    /// The runtime was the third group of a Settings page you had to navigate
    /// to. It is now pinned to the bottom of the sidebar, permanently on screen.
    /// </summary>
    [Fact]
    public void The_runtime_is_always_on_screen()
    {
        var cut = RenderWorkspace();

        var runtime = cut.Find(".ws-runtime");
        Assert.Single(runtime.QuerySelectorAll(".dot"));
        Assert.Contains("on device", runtime.TextContent);
        Assert.False(string.IsNullOrWhiteSpace(runtime.QuerySelector(".ws-runtime-name")!.TextContent));
    }

    // ── What the sidebar must not be ──────────────────────────────────────

    /// <summary>
    /// A bottom tab bar was added to this desktop app and then spent an hour
    /// falling off the bottom of a 768px screen before being removed. Neither
    /// reference has one, and it must not come back by accident.
    /// </summary>
    [Fact]
    public void There_is_no_tab_bar_and_no_overflow_menu()
    {
        var cut = RenderWorkspace();

        Assert.Empty(cut.FindAll(".app-tabs"));
        Assert.Empty(cut.FindAll(".app-tab"));
        Assert.Empty(cut.FindAll(".appmenu"));
        Assert.Empty(cut.FindAll(".appmenu-trigger"));
    }
}
