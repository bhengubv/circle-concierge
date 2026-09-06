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
    private readonly Concierge.Shared.Tools.InteractiveToolApprovalService _approver = new();

    /// <summary>
    /// Blocks a tool call on a person, exactly as the tool layer does.
    ///
    /// Discarded rather than awaited: the call stays pending until answered,
    /// which is the state under test. Raised before rendering so the component
    /// reads it during initialisation and nothing depends on an event crossing
    /// threads mid-test.
    /// </summary>
    private void Raise(string summary, Concierge.Shared.ConciergeToolRisk risk)
        => _ = _approver.RequestAsync(
            new Concierge.Shared.Tools.ToolApprovalRequest("write_file", summary, risk));

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> RenderWorkspace()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sidebar-{Guid.NewGuid():N}.db");
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

        // The real queue. Approvals used to come from two hardcoded entries in
        // ConciergeStateService, so these tests asserted on a fiction; they now
        // raise actual tool calls and assert on what is genuinely blocked.
        Services.AddSingleton<Concierge.Shared.Tools.InteractiveToolApprovalService>(_ => _approver);
        Services.AddSingleton<Concierge.Shared.Tools.IToolApprovalService>(_ => _approver);
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

        // Rendered, then waited for.
        //
        // Render returns after the first pass, and the workspace loads its threads
        // in OnInitializedAsync — so on a loaded machine every assertion made
        // straight afterwards is racing that load. Five tests were fixed one at a
        // time before it was obvious that the fixture was the defect: each looked
        // like an isolated flake, each passed on retry, and the sixth was always
        // going to be somebody else's afternoon.
        //
        // The sidebar is the thing to wait on because every recipe has one and it
        // is populated from the load. Anything asserted after this is looking at a
        // workspace that has finished arriving.
        var rendered = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();
        rendered.WaitForState(() => rendered.FindAll("section.ws-group").Count > 0);
        return rendered;
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
    /// only thing telling you a decision is outstanding.
    /// </summary>
    [Fact]
    public void A_folded_group_still_shows_its_count()
    {
        Raise("Write notes.md", Concierge.Shared.ConciergeToolRisk.High);
        Raise("Run git status", Concierge.Shared.ConciergeToolRisk.Low);

        var cut = RenderWorkspace();

        var group = cut.FindAll("section.ws-group")[1];
        Assert.DoesNotContain("is-open", group.ClassName);

        Assert.Equal("2", group.QuerySelector(".ws-head-count")!.TextContent.Trim());
    }

    /// <summary>
    /// And with nothing waiting it says zero.
    ///
    /// This is the test that would have caught the fiction: the count was two
    /// on a fresh install, before anything had run, because two approvals were
    /// hardcoded into the snapshot.
    /// </summary>
    [Fact]
    public void With_nothing_waiting_the_count_is_zero()
    {
        var cut = RenderWorkspace();

        var group = cut.FindAll("section.ws-group")[1];
        Assert.Equal("0", group.QuerySelector(".ws-head-count")!.TextContent.Trim());

        Header(cut, "Approvals").Click();

        // Re-found after the click: the earlier reference is to the element as
        // it was before the group unfolded.
        Assert.Contains("Nothing waiting", cut.FindAll("section.ws-group")[1].TextContent);
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
        Raise("Write notes.md", Concierge.Shared.ConciergeToolRisk.High);
        Raise("Edit config", Concierge.Shared.ConciergeToolRisk.Medium);

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
        Raise("Write notes.md", Concierge.Shared.ConciergeToolRisk.High);
        Raise("Run git status", Concierge.Shared.ConciergeToolRisk.Low);

        var cut = RenderWorkspace();
        Header(cut, "Approvals").Click();

        var titles = cut.FindAll("section.ws-group")[1]
            .QuerySelectorAll(".ws-item-label")
            .Select(e => e.TextContent.Trim())
            .ToArray();

        Assert.Contains("Write notes.md", titles);
        Assert.Contains("Run git status", titles);
    }

    // ── Threads ───────────────────────────────────────────────────────────

    [Fact]
    public void With_no_conversations_the_threads_group_offers_a_new_one()
    {
        var cut = RenderWorkspace();

        // Waited for rather than read once. The thread list is loaded in
        // OnInitializedAsync, so the first render can carry an empty group and
        // the row arrives on the render after it — which showed up exactly
        // once in a hundred runs and then passed on its own, which is the
        // worst way for a test to be wrong.
        cut.WaitForAssertion(() =>
        {
            var threads = cut.FindAll("section.ws-group")[0];
            var labels = threads.QuerySelectorAll(".ws-item-label").Select(e => e.TextContent.Trim()).ToArray();

            Assert.Equal(new[] { "New thread" }, labels);
        });
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

        // The real queue. Approvals used to come from two hardcoded entries in
        // ConciergeStateService, so these tests asserted on a fiction; they now
        // raise actual tool calls and assert on what is genuinely blocked.
        Services.AddSingleton<Concierge.Shared.Tools.InteractiveToolApprovalService>(_ => _approver);
        Services.AddSingleton<Concierge.Shared.Tools.IToolApprovalService>(_ => _approver);
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
        Assert.False(string.IsNullOrWhiteSpace(runtime.QuerySelector(".ws-runtime-name")!.TextContent));
    }

    /// <summary>
    /// And it does not claim to be local when it is not.
    ///
    /// This line read "on device" underneath whichever engine was selected,
    /// including the three that are a cloud provider — the sidebar contradicting
    /// the product's main claim in the one place somebody would look to check it.
    /// The previous version of this test asserted the constant, which is how a
    /// passing suite can describe a screen that is lying.
    ///
    /// With no runtime registered at all — this fixture's case — there is no
    /// honest version of either label, so there is none.
    /// </summary>
    [Fact]
    public void The_sidebar_does_not_claim_to_be_local_without_a_local_engine()
    {
        var cut = RenderWorkspace();

        Assert.DoesNotContain("on device", cut.Find(".ws-runtime").TextContent);
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
