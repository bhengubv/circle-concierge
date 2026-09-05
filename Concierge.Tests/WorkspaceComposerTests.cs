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
    /// a tab as an entire screen; this is the same control, in the same place,
    /// and its label says which way round it currently is.
    /// </summary>
    [Fact]
    public void Whether_it_may_act_without_asking_is_a_switch_under_the_composer()
    {
        var cut = RenderWith(runtimeReady: true);

        var toggle = cut.Find(".comp-controls .toggle input[type=checkbox]");
        var label = cut.Find(".comp-controls .toggle-label");

        Assert.True(toggle.HasAttribute("checked"));
        Assert.Equal("May act on its own", label.TextContent.Trim());

        toggle.Change(false);

        Assert.Equal("Ask before acting", cut.Find(".comp-controls .toggle-label").TextContent.Trim());
    }

    /// <summary>
    /// It is a switch, not the operating system's tick box. A raw checkbox
    /// renders Windows' own blue control, which belongs to no design system.
    /// </summary>
    [Fact]
    public void The_switch_is_drawn_not_borrowed_from_the_platform()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.NotNull(cut.Find(".toggle .toggle-track .toggle-knob"));
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

        Assert.Contains("Attach a text file", names);
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

        Assert.NotNull(cut.Find(".comp-box textarea.comp-input"));
        Assert.NotNull(cut.Find(".empty"));
        Assert.Empty(cut.FindAll(".ws-thread"));
    }
}
