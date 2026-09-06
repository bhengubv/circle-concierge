using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The composer: the input, its three controls, and the two things Codex puts
/// directly beneath it — whether the assistant may act without asking, and which
/// model is answering. Both of those were entire screens in this app before.
///
/// Synchronous by design; see WorkspaceHandoffTests for why.
/// </summary>
public sealed class WorkspaceComposerTests : BunitContext
{
    /// <summary>
    /// A runtime that is simply ready. CanSend gates on IsReady, so without one
    /// the send control can never enable and half of these tests would assert
    /// nothing. It never streams — no test here asks it to answer, which also
    /// keeps them off the native inference path that faults on first call.
    /// </summary>
    private sealed class ReadyRuntime : IChatRuntime
    {
        public string Id => "test";
        public string EngineLabel => "Test-Engine-7B (stub)";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> RenderWith(bool runtimeReady)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"composer-{Guid.NewGuid():N}.db");
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
        Services.AddSingleton<IEnumerable<IChatRuntime>>(_ =>
            runtimeReady ? new IChatRuntime[] { new ReadyRuntime() } : Array.Empty<IChatRuntime>());
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

        return Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();
    }

    private static IElement Send(IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
        => cut.Find("button.icon-btn-send");

    // ── When you can send ─────────────────────────────────────────────────

    [Fact]
    public void Send_is_disabled_with_an_empty_composer()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Typing_enables_send()
    {
        var cut = RenderWith(runtimeReady: true);

        cut.Find("textarea.comp-input").Input("Say hello.");

        Assert.False(Send(cut).HasAttribute("disabled"));
    }

    /// <summary>
    /// Nothing can be sent to a runtime that has not finished loading, however
    /// much has been typed. This is the guard that stops a prompt vanishing into
    /// a model that is not there yet.
    /// </summary>
    [Fact]
    public void Send_stays_disabled_while_no_runtime_is_ready()
    {
        var cut = RenderWith(runtimeReady: false);

        cut.Find("textarea.comp-input").Input("Say hello.");

        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Whitespace_alone_does_not_enable_send()
    {
        var cut = RenderWith(runtimeReady: true);

        cut.Find("textarea.comp-input").Input("    ");

        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    // ── The controls beneath the input ────────────────────────────────────

    /// <summary>
    /// Codex puts "Ask for approval" under the composer. Concierge had it behind
    /// a tab as an entire screen; this is the same decision, in the same place.
    ///
    /// It was a switch, and a switch could not tell the truth here. Off did not
    /// mean "ask" — it meant tools never ran at all — and on still asked before
    /// every write, because the tools ask for themselves. Three real states
    /// need three choices.
    /// </summary>
    [Fact]
    public void What_it_may_do_is_chosen_under_the_composer()
    {
        var cut = RenderWith(runtimeReady: true);

        var options = cut.FindAll(".comp-controls .seg .seg-opt")
            .Select(e => e.TextContent.Trim())
            .ToArray();

        Assert.Equal(new[] { "Plan only", "Ask first", "Act freely" }, options);
    }

    /// <summary>
    /// Asking first is the default, because it is the product's actual promise.
    /// </summary>
    [Fact]
    public void It_asks_first_unless_told_otherwise()
    {
        var cut = RenderWith(runtimeReady: true);

        var chosen = cut.FindAll(".comp-controls .seg .seg-opt")
            .Single(e => e.GetAttribute("aria-pressed") == "true");

        Assert.Equal("Ask first", chosen.TextContent.Trim());
    }

    [Fact]
    public void Choosing_a_mode_marks_it()
    {
        var cut = RenderWith(runtimeReady: true);

        cut.FindAll(".comp-controls .seg .seg-opt").Single(e => e.TextContent.Trim() == "Plan only").Click();

        var chosen = cut.FindAll(".comp-controls .seg .seg-opt")
            .Single(e => e.GetAttribute("aria-pressed") == "true");

        Assert.Equal("Plan only", chosen.TextContent.Trim());
    }

    /// <summary>
    /// Each says what it means. A person choosing here is deciding what
    /// software may do to their machine, and three verbs alone do not say.
    /// </summary>
    [Fact]
    public void Each_mode_explains_itself()
    {
        var cut = RenderWith(runtimeReady: true);

        foreach (var option in cut.FindAll(".comp-controls .seg .seg-opt"))
        {
            Assert.False(string.IsNullOrWhiteSpace(option.GetAttribute("title")));
        }
    }

    /// <summary>
    /// Which model is answering used to be the third group of a Settings page.
    /// </summary>
    [Fact]
    public void The_model_answering_is_named_under_the_composer()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.Contains("Test-Engine-7B (stub)", cut.Find(".comp-controls").TextContent);
    }

    // ── The three icon controls ───────────────────────────────────────────

    /// <summary>
    /// InputFile renders a bare file input, which on Windows draws a grey
    /// "Choose File / No file chosen" button. It is stretched invisibly over the
    /// paperclip instead — same element, same behaviour, no platform furniture.
    /// </summary>
    [Fact]
    public void The_attach_control_hides_the_platform_file_input()
    {
        var cut = RenderWith(runtimeReady: true);

        var input = cut.Find("input[type=file].file-over");
        Assert.Equal("LABEL", input.ParentElement!.TagName);
        Assert.Contains("icon-btn", input.ParentElement.ClassName);
    }

    /// <summary>
    /// Every icon control keeps its name for a screen reader even though only the
    /// glyph is shown.
    /// </summary>
    [Fact]
    public void Every_icon_control_still_has_a_name()
    {
        var cut = RenderWith(runtimeReady: true);

        var names = cut.FindAll(".comp-box .sr").Select(e => e.TextContent.Trim()).ToArray();

        // Renamed when the picker started accepting pictures too.
        Assert.Contains("Attach a file or a picture", names);
        Assert.Contains("Speak", names);
        Assert.Contains("Send", names);
    }

    [Fact]
    public void The_composer_invites_you_to_type()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.Equal("Ask me anything", cut.Find("textarea.comp-input").GetAttribute("placeholder"));
    }

    // ── The empty screen ──────────────────────────────────────────────────

    /// <summary>
    /// The composer is present with no conversation open. It used to appear only
    /// once a conversation existed, so opening Chat gave you a heading and a
    /// button whose only job was to reveal the thing you came for.
    /// </summary>
    [Fact]
    public void The_composer_is_there_before_any_conversation_exists()
    {
        var cut = RenderWith(runtimeReady: true);

        // Waited for, not read once. The empty state is only correct after the
        // thread list has loaded in OnInitializedAsync, so under a loaded machine
        // the first render can be neither empty nor populated — the same flake the
        // sidebar had, seen about once in a hundred runs and passing on retry,
        // which is the worst way for a test to be wrong.
        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find(".comp-box textarea.comp-input"));
            Assert.NotNull(cut.Find(".empty"));
            Assert.Empty(cut.FindAll(".ws-thread"));
        });
    }

    // ── The composer while a canvas is open ───────────────────────────────

    /// <summary>
    /// Opens the design canvas the way a person does — the sidebar entry.
    /// </summary>
    private static void OpenDesign(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
    {
        var tools = cut.FindAll("button.ws-head")
            .First(b => b.QuerySelector(".ws-head-name")!.TextContent.Trim() == "Tools");
        tools.Click();

        cut.WaitForState(() => cut.FindAll("button.ws-room-btn").Count > 0);
        cut.FindAll("button.ws-room-btn").First(b => b.TextContent.Contains("Design")).Click();
        cut.WaitForState(() => cut.FindAll(".dz").Count > 0);
    }

    /// <summary>
    /// The worst of them. Closing the canvas threw the whole design away — one
    /// click, an afternoon gone, no warning and nothing to go back to. The surface
    /// is built on the promise that every state you have seen is one tap away, and
    /// its own close button was breaking it.
    /// </summary>
    [Fact]
    public void Closing_the_canvas_puts_it_away_rather_than_throwing_it_out()
    {
        var cut = RenderWith(runtimeReady: true);
        OpenDesign(cut);

        cut.Find("textarea.comp-input").Input("add a title that says Sports Day");
        cut.Find("button.icon-btn-send").Click();
        cut.WaitForState(() => cut.FindAll(".dz-moment").Count > 1);

        var moments = cut.FindAll(".dz-moment").Count;

        // Away, and back again.
        cut.FindAll("button.ws-room-btn").First(b => b.TextContent.Contains("Design")).Click();
        cut.WaitForState(() => cut.FindAll(".dz").Count == 0);
        cut.FindAll("button.ws-room-btn").First(b => b.TextContent.Contains("Design")).Click();
        cut.WaitForState(() => cut.FindAll(".dz").Count > 0);

        Assert.Equal(moments, cut.FindAll(".dz-moment").Count);
    }

    /// <summary>
    /// "Ask me anything" while a canvas is open asks for something that is not
    /// going to happen: nothing is being asked and no model is answering.
    /// </summary>
    [Fact]
    public void The_composer_stops_asking_you_to_ask_it_anything()
    {
        var cut = RenderWith(runtimeReady: true);
        OpenDesign(cut);

        Assert.Contains("on the page", cut.Find("textarea.comp-input").GetAttribute("placeholder"));
    }

    /// <summary>
    /// The permission modes and the engine name are both about a model answering,
    /// and with a canvas open none is. Worse than useless: the row said "Ask first"
    /// while Design deliberately acts without asking, advertising the opposite of
    /// what was happening.
    /// </summary>
    [Fact]
    public void The_permission_modes_are_not_offered_where_they_do_not_apply()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.NotEmpty(cut.FindAll(".seg-opt"));

        OpenDesign(cut);

        Assert.Empty(cut.FindAll(".seg-opt"));
    }

    /// <summary>
    /// The canvas needs no engine, so gating it on one would leave Design dead
    /// while a model loads — or permanently, on a machine where the model is
    /// broken, which is this one.
    /// </summary>
    [Fact]
    public void The_canvas_works_with_no_engine_at_all()
    {
        var cut = RenderWith(runtimeReady: false);
        OpenDesign(cut);

        cut.Find("textarea.comp-input").Input("add a title that says It works");

        Assert.False(cut.Find("button.icon-btn-send").HasAttribute("disabled"));
    }
}
