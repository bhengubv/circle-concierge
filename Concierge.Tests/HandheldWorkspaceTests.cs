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
    private IRenderedComponent<Concierge.Shared.Components.Workspace.Handheld.Workspace> RenderWorkspace()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"handheld-{Guid.NewGuid():N}.db");
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

        return Render<Concierge.Shared.Components.Workspace.Handheld.Workspace>();
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
        Header(cut, "Skills").Click();
        cut.Find("button.ws-more").Click();

        Assert.DoesNotContain("is-open", cut.Find("aside.ws-side").ClassName);
    }
}
