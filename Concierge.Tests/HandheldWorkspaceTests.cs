using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The handheld recipe: tablet and phone.
///
/// Its own component set, not the desktop tree with breakpoints. The visible
/// difference is the sidebar — a drawer over the thread rather than a column
/// beside it, because a phone cannot afford a permanent 220px column. These
/// tests are here rather than in WorkspaceSidebarTests because the desktop
/// recipe has no drawer at all: no menu control, no scrim, no open state.
///
/// bUnit has no viewport, so nothing here knows which recipe a real device
/// would get — that is Pages/Chat.razor's job and is tested separately. What
/// this asserts is that the handheld recipe behaves once chosen.
///
/// Synchronous by design; see WorkspaceHandoffTests for why.
/// </summary>
public sealed class HandheldWorkspaceTests : BunitContext
{
    [Fact]
    public void Mobile_brand_identifies_Concierge_as_the_product_and_Circle_AI_as_its_engine()
    {
        var cut = RenderWorkspace();

        Assert.Equal("Concierge", cut.Find(".ws-brand-lockup strong").TextContent.Trim());
        Assert.Equal("by Circle", cut.Find(".ws-brand-lockup small").TextContent.Trim());
        Assert.Equal("Concierge", cut.Find(".welcome-brand").TextContent.Trim());
        Assert.Equal("Powered by Circle AI", cut.Find(".welcome-engine").TextContent.Trim());
        Assert.Equal("AI engine", cut.Find(".ws-ai-title").TextContent.Trim());
        Assert.Contains("Circle AI", cut.Find(".ws-runtime-name").TextContent);
    }

    [Fact]
    public void Mobile_sidebar_has_a_projects_entry_for_bigger_goals()
    {
        var cut = RenderWorkspace();

        var link = cut.Find("a.ws-project-link");
        Assert.Equal("/projects", link.GetAttribute("href"));
        Assert.Contains("Organize bigger goals", link.TextContent);
    }

    [Fact]
    public void Mobile_home_explains_the_reachability_needed_for_a_Desktop_connection()
    {
        var cut = RenderWorkspace();

        Assert.Contains("Connect to your Desktop", cut.Find(".welcome-desktop-link summary").TextContent);
        Assert.Equal("url", cut.Find("#welcome-desktop-link-input").GetAttribute("type"));
        Assert.Contains("works only while both devices are on that network", cut.Find(".welcome-desktop-link").TextContent);
        Assert.Contains("internet or your VPN", cut.Find(".welcome-desktop-link").TextContent);
        Assert.Equal("Desktop address", cut.Find("label[for='welcome-desktop-link-input']").TextContent);
        Assert.Contains("http://", cut.Find("#welcome-desktop-link-input").GetAttribute("placeholder"));
    }

    [Fact]
    public void Saved_Desktop_link_has_an_enabled_connect_action()
    {
        var desktop = new Concierge.Away.AwayClient(
            hostConfigPath: Path.Combine(Path.GetTempPath(), $"mobile-desktop-link-{Guid.NewGuid():N}.txt"));
        desktop.ConfigureHost("http://192.168.0.250:5198");

        var cut = RenderWorkspace(desktopClient: desktop);

        Assert.Equal("http://192.168.0.250:5198", cut.Find("#welcome-desktop-link-input").GetAttribute("value"));
        Assert.False(cut.Find(".welcome-desktop-link button[type='submit']").HasAttribute("disabled"));
    }

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Handheld.Workspace> RenderWorkspace(
        Guid? conversationId = null, CancellationTokenSource? activeRun = null,
        Concierge.Away.AwayClient? desktopClient = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"handheld-{Guid.NewGuid():N}.db");
        Services.AddLogging();
        Services.AddConciergeCore();
        Services.AddSingleton<Concierge.Shared.Settings.IConciergeSecretStore, InMemorySecretStore>();

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
        if (desktopClient is not null)
            Services.AddSingleton(desktopClient);
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
        if (desktopClient is not null)
            JSInterop.Setup<bool>("conciergeSense.isWindowsDesktop").SetResult(true);

        using (var db = Services.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>().CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        if (activeRun is not null)
        {
            var conversation = Services.GetRequiredService<IConversationStore>()
                .StartAsync("local", "Live Mobile work").GetAwaiter().GetResult();
            conversationId = conversation.Id;
            Services.GetRequiredService<BackgroundRuns>().Started(conversationId.Value, activeRun);
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
        var rendered = Render<Concierge.Shared.Components.Workspace.Handheld.Workspace>(parameters =>
            parameters.Add(component => component.ConversationId, conversationId));
        rendered.WaitForState(() => rendered.FindAll("section.ws-group").Count > 0);
        return rendered;
    }

    [Fact]
    public void Mobile_active_run_has_a_stop_control_that_cancels_it()
    {
        using var cancellation = new CancellationTokenSource();
        var cut = RenderWorkspace(activeRun: cancellation);
        cut.Find("button[aria-label='Stop this run']").Click();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains("Stopping this run", cut.Markup);
    }

    [Fact]
    public void Mobile_shows_Desktop_model_download_even_while_Desktop_conversation_is_available()
    {
        var cut = RenderWorkspace();
        var now = DateTimeOffset.UtcNow;
        var catalog = new ChatRuntimeCapabilityCatalog(
            SchemaVersion: 1,
            RuntimeId: "circleai",
            RuntimeVersion: "3.6.0",
            ObservedAt: now,
            Capabilities: [new ChatRuntimeCapability("conversation", "Conversation", ChatRuntimeCapabilityState.Available)],
            Resources: null,
            Activity: new ChatRuntimeActivity("model-download", "Model 9 of 10 · weights 4 GB / 5 GB", .8));

        var workspaceBase = typeof(Concierge.Shared.Components.Workspace.WorkspaceBase);
        workspaceBase.GetField("_desktopCapabilityCatalog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(cut.Instance, catalog);
        workspaceBase.GetField("_desktopAvailable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(cut.Instance, true);
        cut.Render();

        var progress = cut.Find("progress[aria-label='Circle AI model download progress on Desktop']");
        Assert.Equal("0.8", progress.GetAttribute("value"));
        Assert.Contains("Model 9 of 10 · weights 4 GB / 5 GB on your Desktop", cut.Find(".model-download-status").TextContent);
    }

    private static IElement Header(IRenderedComponent<Concierge.Shared.Components.Workspace.Handheld.Workspace> cut, string name)
        => cut.FindAll("button.ws-head")
              .First(b => b.QuerySelector(".ws-head-name")!.TextContent.Trim() == name);

    // ── The drawer ────────────────────────────────────────────────────────

    /// <summary>
    /// Below 760px the sidebar is a drawer over the thread rather than a column
    /// beside it. Same markup, same groups, different presentation — there is
    /// one .ws-side and one set of group headers, not a second mobile menu to
    /// keep in step with this one.
    ///
    /// Note what bUnit can and cannot see here: there is no viewport and no
    /// stylesheet, so nothing below knows which presentation is in effect. It
    /// asserts the state and the controls; that the drawer actually slides in
    /// at 404px and stays out of the way at 993px is measured on the running
    /// desktop app.
    /// </summary>
    [Fact]
    public void There_is_one_sidebar_and_not_a_separate_mobile_menu()
    {
        var cut = RenderWorkspace();

        Assert.Single(cut.FindAll("aside.ws-side"));
        Assert.Equal(5, cut.FindAll("aside.ws-side button.ws-head").Count);
        Assert.Single(cut.FindAll("button.ws-menu"));
    }

    [Fact]
    public void The_drawer_starts_closed()
    {
        var cut = RenderWorkspace();

        Assert.DoesNotContain("is-open", cut.Find("aside.ws-side").ClassName);
        Assert.Empty(cut.FindAll("button.ws-scrim"));
        Assert.Equal("false", cut.Find("button.ws-menu").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void The_menu_control_opens_the_drawer()
    {
        var cut = RenderWorkspace();

        cut.Find("button.ws-menu").Click();

        Assert.Contains("is-open", cut.Find("aside.ws-side").ClassName);
        Assert.Equal("true", cut.Find("button.ws-menu").GetAttribute("aria-expanded"));
        Assert.NotNull(cut.Find("button.ws-scrim"));
    }

    /// <summary>
    /// Dismissing by pressing the work you can still see behind it.
    /// </summary>
    [Fact]
    public void Pressing_the_scrim_closes_the_drawer()
    {
        var cut = RenderWorkspace();
        cut.Find("button.ws-menu").Click();

        cut.Find("button.ws-scrim").Click();

        Assert.DoesNotContain("is-open", cut.Find("aside.ws-side").ClassName);
        Assert.Empty(cut.FindAll("button.ws-scrim"));
    }

    /// <summary>
    /// The two controls in the drawer that open a panel rather than navigate.
    /// Leaving the drawer open under a panel means dismissing two things to get
    /// back to the work.
    /// </summary>
    [Fact]
    public void Opening_a_panel_from_the_drawer_closes_it()
    {
        var cut = RenderWorkspace();

        cut.Find("button.ws-menu").Click();
        cut.Find("button.ws-runtime").Click();

        Assert.DoesNotContain("is-open", cut.Find("aside.ws-side").ClassName);
        Assert.NotNull(cut.Find(".sheet"));

        // And the same for the skills picker.
        cut.Find(".sheet-head button.icon-btn").Click();
        cut.Find("button.ws-menu").Click();
        Header(cut, "Personalize Circle AI").Click();
        cut.Find("button.ws-more").Click();

        Assert.DoesNotContain("is-open", cut.Find("aside.ws-side").ClassName);
    }
}
