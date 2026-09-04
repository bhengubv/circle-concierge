using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// Skills: something you switch on, not a catalogue you read.
///
/// An active skill composes into the system prompt through ISkillRuntime before
/// every turn, so turning one on changes what the assistant is for the rest of
/// the conversation. The machinery was already here; what was missing was any
/// way to use it — the old page listed sixty-nine names and did nothing at all.
///
/// Synchronous by design; see WorkspaceHandoffTests for why.
/// </summary>
public sealed class WorkspaceSkillsTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> RenderWorkspace()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}.db");
        Services.AddLogging();
        Services.AddConciergeCore();
        Services.AddConciergeChat(dbPath);
        Services.AddSingleton<IEnumerable<IChatRuntime>>(_ => Array.Empty<IChatRuntime>());
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeTools(Services);
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeRuntime(Services);
        Concierge.Shared.Diagrams.ConciergeDiagramsServiceCollectionExtensions.AddConciergeDiagrams(Services);
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

    private static void OpenSkillsGroup(IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
        => cut.FindAll("button.ws-head")
              .First(b => b.QuerySelector(".ws-head-name")!.TextContent.Trim() == "Skills")
              .Click();

    private static void OpenPicker(IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
    {
        OpenSkillsGroup(cut);
        cut.Find("button.ws-more").Click();
    }

    // ── The sidebar shows what is on ──────────────────────────────────────

    /// <summary>
    /// The count is of what is ON, not of what exists. Sixty-nine available
    /// tells you nothing; two shaping every answer tells you a great deal.
    /// </summary>
    [Fact]
    public void The_sidebar_counts_what_is_on_not_what_exists()
    {
        var cut = RenderWorkspace();

        var header = cut.FindAll("button.ws-head")
            .First(b => b.QuerySelector(".ws-head-name")!.TextContent.Trim() == "Skills");

        Assert.Equal("0", header.QuerySelector(".ws-head-count")!.TextContent.Trim());
    }

    [Fact]
    public void With_nothing_on_the_sidebar_says_so_and_offers_the_catalogue()
    {
        var cut = RenderWorkspace();
        OpenSkillsGroup(cut);

        var group = cut.FindAll("section.ws-group")[2];
        Assert.Contains("None on", group.TextContent);
        Assert.Contains("Browse all", group.QuerySelector("button.ws-more")!.TextContent);
    }

    // ── The picker ────────────────────────────────────────────────────────

    [Fact]
    public void The_catalogue_opens_as_a_panel_over_the_work()
    {
        var cut = RenderWorkspace();
        OpenPicker(cut);

        Assert.Equal("Skills", cut.Find(".sheet-title").TextContent.Trim());
        Assert.NotNull(cut.Find(".ws-side"));
        Assert.NotNull(cut.Find(".comp-box"));
    }

    /// <summary>
    /// Sixty-nine of anything is a search problem. Note what this can and cannot
    /// check: bUnit has no layout engine, so it asserts the CAP and never the
    /// fit. Twelve rows passed this assertion happily while running the panel
    /// 664px past the bottom of the window — only the real app showed that.
    /// </summary>
    [Fact]
    public void The_catalogue_shows_a_page_of_results_not_all_of_them()
    {
        var cut = RenderWorkspace();
        OpenPicker(cut);

        var rows = cut.FindAll(".skills-list .skill-row");
        Assert.True(rows.Count <= 5, $"expected at most 5 rows in the panel, found {rows.Count}");
        Assert.Contains("more — keep typing", cut.Find(".sheet").TextContent);
    }

    [Fact]
    public void Typing_narrows_the_catalogue()
    {
        var cut = RenderWorkspace();
        OpenPicker(cut);

        var before = cut.Find(".sheet-count .row-val").TextContent;

        cut.Find("input.skills-find").Input("accessibility");

        var after = cut.Find(".sheet-count .row-val").TextContent;
        Assert.NotEqual(before, after);
        Assert.All(cut.FindAll(".skills-list .skill-row"),
                   r => Assert.Contains("ccessibilit", r.TextContent, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_search_that_matches_nothing_says_so()
    {
        var cut = RenderWorkspace();
        OpenPicker(cut);

        cut.Find("input.skills-find").Input("zzzzzznotaskill");

        Assert.Empty(cut.FindAll(".skills-list .skill-row"));
        Assert.Contains("Nothing matches", cut.Find(".sheet").TextContent);
    }

    // ── Switching one on ──────────────────────────────────────────────────

    /// <summary>
    /// The whole point: pressing a skill turns it on, and turning it on is
    /// visible everywhere it matters — the sidebar count, the composer, and the
    /// row itself.
    /// </summary>
    [Fact]
    public void Turning_a_skill_on_shows_everywhere_it_matters()
    {
        var cut = RenderWorkspace();
        OpenPicker(cut);

        cut.FindAll(".skills-list .skill-row")[0].Click();

        // The row says so.
        Assert.Contains(cut.FindAll(".skills-list .skill-row"),
                        r => r.GetAttribute("aria-pressed") == "true");

        cut.Find(".sheet-head button.icon-btn").Click();

        // The sidebar counts it.
        var header = cut.FindAll("button.ws-head")
            .First(b => b.QuerySelector(".ws-head-name")!.TextContent.Trim() == "Skills");
        Assert.Equal("1", header.QuerySelector(".ws-head-count")!.TextContent.Trim());

        // And it sits with the composer, because it shapes every reply from it.
        Assert.NotEmpty(cut.FindAll(".comp-inner .chips .chip"));
    }

    [Fact]
    public void Pressing_it_again_turns_it_off()
    {
        var cut = RenderWorkspace();
        OpenPicker(cut);

        cut.FindAll(".skills-list .skill-row")[0].Click();
        cut.FindAll(".skills-list .skill-row").First(r => r.GetAttribute("aria-pressed") == "true").Click();

        Assert.DoesNotContain(cut.FindAll(".skills-list .skill-row"),
                              r => r.GetAttribute("aria-pressed") == "true");
    }

    [Fact]
    public void A_skill_can_be_turned_off_from_the_composer()
    {
        var cut = RenderWorkspace();
        OpenPicker(cut);
        cut.FindAll(".skills-list .skill-row")[0].Click();
        cut.Find(".sheet-head button.icon-btn").Click();

        cut.Find(".comp-inner .chips .chip").Click();

        Assert.Empty(cut.FindAll(".comp-inner .chips .chip"));
    }

    /// <summary>
    /// What is on rises to the top, so the thing you are most likely to want to
    /// turn off is not buried behind sixty-eight others.
    /// </summary>
    [Fact]
    public void What_is_on_sorts_to_the_top_of_the_catalogue()
    {
        var cut = RenderWorkspace();
        OpenPicker(cut);

        // Turn on something that is not already first alphabetically. The last
        // row on the page, so this keeps working whatever the page size is —
        // indexing past it is how this broke when the cap went from 8 to 5.
        cut.FindAll(".skills-list .skill-row").Last().Click();

        Assert.Equal("true", cut.FindAll(".skills-list .skill-row")[0].GetAttribute("aria-pressed"));
    }
}
