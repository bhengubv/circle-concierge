using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// Settings, as a panel over the work rather than a place you go.
///
/// Neither reference has a settings screen you travel to: Claude Design floats a
/// dark Tweaks panel over the canvas, and Codex keeps the two settings that
/// matter — approval and model — in the composer. Concierge moved those two
/// already; what is left is everything you change rarely, and changing it should
/// not mean leaving the conversation.
///
/// Synchronous by design; see WorkspaceHandoffTests for why.
/// </summary>
public sealed class WorkspaceSettingsTests : BunitContext
{
    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> RenderWorkspace()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.db");
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

    private static void Open(IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
        => cut.Find("button.ws-runtime").Click();

    /// <summary>Unfolds a settings group by its label.</summary>
    private static void OpenGroup(IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut, string label)
    {
        var header = cut.FindAll("button.sheet-head-btn")
            .First(b => b.QuerySelector(".sheet-label")!.TextContent.Trim() == label);
        if (header.QuerySelector(".ws-chev")!.ClassName!.Contains("is-open"))
        {
            return;
        }

        header.Click();
    }

    // ── Opening and closing ───────────────────────────────────────────────

    [Fact]
    public void Settings_is_not_on_screen_until_it_is_asked_for()
    {
        var cut = RenderWorkspace();

        Assert.Empty(cut.FindAll(".sheet"));
    }

    /// <summary>
    /// Pressing what is answering, to change what answers. The runtime row is
    /// the way in, which is the shortest true sentence about where that control
    /// belongs.
    /// </summary>
    [Fact]
    public void The_runtime_row_opens_settings()
    {
        var cut = RenderWorkspace();

        Open(cut);

        Assert.NotNull(cut.Find(".sheet"));
        Assert.Equal("Settings", cut.Find(".sheet-title").TextContent.Trim());
    }

    [Fact]
    public void It_is_a_panel_over_the_work_not_a_page()
    {
        var cut = RenderWorkspace();

        Open(cut);

        // Over the workspace: the sidebar and composer are still rendered
        // underneath, so nothing was navigated away from.
        Assert.NotNull(cut.Find(".sheet-back"));
        Assert.NotNull(cut.Find(".ws-side"));
        Assert.NotNull(cut.Find(".comp-box"));
    }

    [Fact]
    public void The_close_control_dismisses_it()
    {
        var cut = RenderWorkspace();
        Open(cut);

        cut.Find(".sheet-head button.icon-btn").Click();

        Assert.Empty(cut.FindAll(".sheet"));
    }

    [Fact]
    public void Pressing_the_backdrop_dismisses_it()
    {
        var cut = RenderWorkspace();
        Open(cut);

        cut.Find(".sheet-back").Click();

        Assert.Empty(cut.FindAll(".sheet"));
    }

    // ── The controls ──────────────────────────────────────────────────────

    /// <summary>
    /// Strictness is a segmented picker, not a dropdown: three options visible,
    /// one press to change. Both references use a segmented control for a choice
    /// this small, and a &lt;select&gt; is two presses and a hidden list.
    /// </summary>
    [Fact]
    public void Strictness_is_a_segmented_picker_and_not_a_dropdown()
    {
        var cut = RenderWorkspace();
        Open(cut);
        OpenGroup(cut, "Family mode");

        var options = cut.FindAll(".seg .seg-opt").Select(e => e.TextContent.Trim()).ToArray();
        Assert.Equal(new[] { "Off", "Balanced", "Strict" }, options);

        Assert.Empty(cut.FindAll(".sheet select"));
    }

    [Fact]
    public void Choosing_a_strictness_marks_it_and_changes_the_setting()
    {
        var cut = RenderWorkspace();
        var safety = Services.GetRequiredService<ConciergeSafetySettings>();
        Open(cut);
        OpenGroup(cut, "Family mode");

        cut.FindAll(".seg .seg-opt").Single(e => e.TextContent.Trim() == "Strict").Click();

        Assert.Equal(SafetyStrictness.Strict, safety.Strictness);

        var chosen = cut.FindAll(".seg .seg-opt").Single(e => e.GetAttribute("aria-pressed") == "true");
        Assert.Equal("Strict", chosen.TextContent.Trim());
    }

    [Fact]
    public void Kid_mode_is_a_drawn_switch()
    {
        var cut = RenderWorkspace();
        Open(cut);
        OpenGroup(cut, "Family mode");

        Assert.NotNull(cut.Find(".sheet .toggle .toggle-track .toggle-knob"));
    }

    /// <summary>
    /// The profile label only exists once there is something to label — asking
    /// for a child's name before family mode is on is asking for nothing.
    /// </summary>
    [Fact]
    public void The_profile_label_appears_only_once_filtering_is_on()
    {
        var cut = RenderWorkspace();
        var safety = Services.GetRequiredService<ConciergeSafetySettings>();
        Open(cut);
        OpenGroup(cut, "Family mode");

        Assert.Empty(cut.FindAll(".sheet input[type=text]"));

        cut.FindAll(".seg .seg-opt").Single(e => e.TextContent.Trim() == "Balanced").Click();

        Assert.Equal(SafetyStrictness.Balanced, safety.Strictness);
        Assert.NotNull(cut.Find(".sheet input[type=text]"));
    }

    // ── What it shows ─────────────────────────────────────────────────────

    [Fact]
    public void The_limits_are_reported_as_values_not_prose()
    {
        var cut = RenderWorkspace();
        Open(cut);
        OpenGroup(cut, "Limits");

        var names = cut.FindAll(".sheet .row-name").Select(e => e.TextContent).ToArray();
        Assert.Contains(names, n => n.Contains("Jobs at once"));
        Assert.Contains(names, n => n.Contains("Give up after"));

        // Each of those carries a value on the right rather than a sentence.
        Assert.NotEmpty(cut.FindAll(".sheet .row-val"));
    }

    [Fact]
    public void What_is_on_this_device_is_stated_with_a_dot()
    {
        var cut = RenderWorkspace();
        Open(cut);

        OpenGroup(cut, "On this device");

        var labels = cut.FindAll(".sheet .sheet-label").Select(e => e.TextContent.Trim()).ToArray();
        Assert.Contains("On this device", labels);
        Assert.Contains("Who answers", labels);
        Assert.Contains("Family mode", labels);

        Assert.NotEmpty(cut.FindAll(".sheet .dot"));
    }

    /// <summary>
    /// The design rule, in the one screen most likely to break it: status is a
    /// dot and a word. No pills, no badges, no filled cards.
    /// </summary>
    [Fact]
    public void Nothing_in_the_panel_is_a_badge_or_a_pill()
    {
        var cut = RenderWorkspace();
        Open(cut);

        Assert.Empty(cut.FindAll(".sheet .pill"));
        Assert.Empty(cut.FindAll(".sheet .consequence"));
    }

    // ── What the screenshot caught and the tests did not ──────────────────

    /// <summary>
    /// The panel is five groups of rows and floats in a window that is often
    /// shorter than it. Scrollbars are hidden everywhere in this app, so a
    /// cut-off panel gives no sign there is more — the first build ended
    /// mid-way through Family mode with Limits and the runtime below the fold
    /// and nothing to say so. Folding is the same answer the sidebar uses.
    /// </summary>
    [Fact]
    public void Only_one_group_is_unfolded_so_the_panel_fits()
    {
        var cut = RenderWorkspace();
        Open(cut);

        var openChevrons = cut.FindAll(".sheet .ws-chev")
            .Count(c => c.ClassName!.Contains("is-open"));

        Assert.Equal(1, openChevrons);
    }

    [Fact]
    public void A_folded_group_renders_none_of_its_rows()
    {
        var cut = RenderWorkspace();
        Open(cut);

        // Limits is folded on arrival.
        Assert.DoesNotContain(cut.FindAll(".sheet .row-name").Select(e => e.TextContent),
                              n => n.Contains("Jobs at once"));

        OpenGroup(cut, "Limits");

        Assert.Contains(cut.FindAll(".sheet .row-name").Select(e => e.TextContent),
                        n => n.Contains("Jobs at once"));
    }

    /// <summary>
    /// IChatRuntime.StatusMessage reads "Anthropic API key not configured — set
    /// Anthropic:ApiKey in IConfiguration to enable". True, and addressed to
    /// whoever wrote the code: it names a configuration key nobody looking at
    /// this screen can act on. Rebuilding the panel from the code-behind put it
    /// straight back, and only a screenshot caught it.
    /// </summary>
    [Fact]
    public void No_developer_jargon_reaches_the_person_reading_it()
    {
        var cut = RenderWorkspace();
        Open(cut);

        var text = cut.Find(".sheet").TextContent;

        Assert.DoesNotContain("IConfiguration", text);
        Assert.DoesNotContain("ApiKey", text);
        Assert.DoesNotContain("not configured", text);
    }
}
