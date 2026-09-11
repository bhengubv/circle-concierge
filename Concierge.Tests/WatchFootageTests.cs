using System.Runtime.CompilerServices;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// Watching footage and answering questions about it, end to end.
///
/// Every piece of this existed separately and the last join was missing. `media_frames`
/// takes frames from across a video and hands the strip to `CapturedImages`; a turn can carry
/// pictures; three cloud runtimes and now the local one can look at them. What was never
/// checked is the bit in between: that a picture a tool produced actually reaches the model,
/// and — the half that matters more — that it does **not** silently reach one that cannot see.
///
/// A filmstrip handed to a text model and then discussed as though it had been looked at is
/// this repository's signature defect on the one surface whose job is showing what happened.
/// </summary>
public sealed class WatchFootageTests : BunitContext
{
    /// <summary>A runtime that keeps the turns it was given.</summary>
    private sealed class Watching(bool sees) : IChatRuntime, IVisionCapableRuntime
    {
        public IReadOnlyList<ChatTurn> Saw { get; private set; } = [];

        public string Id => "watching";
        public string EngineLabel => "Watching (stub)";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public IReadOnlyCollection<string> SupportedImageMediaTypes => sees ? ["image/png"] : [];

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Saw = messages;
            await Task.CompletedTask;
            yield return "Looked.";
        }
    }

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> RenderWith(
        IChatRuntime runtime)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"footage-{Guid.NewGuid():N}.db");
        Services.AddLogging();
        Services.AddConciergeCore();

        Services.AddSingleton(_ => new Concierge.Shared.Skills.UserSkillFolders(
            Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}.json")));

        Services.AddSingleton<Concierge.Shared.Session.ISessionState>(
            new Concierge.Shared.Session.FileSessionState(
                Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid():N}.json")));

        Services.AddConciergeChat(dbPath);
        Services.AddSingleton<IEnumerable<IChatRuntime>>(_ => new[] { runtime });
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

        var rendered = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();
        rendered.WaitForState(() => rendered.FindAll("section.ws-group").Count > 0);

        return rendered;
    }

    /// <summary>What `media_frames` leaves behind: one strip, waiting for the next turn.</summary>
    private void AFilmstrip()
        => Services.GetRequiredService<CapturedImages>()
            .Add(new ChatImage("clip-frames.png", "image/png", [0x89, 0x50, 0x4E, 0x47]));

    /// <summary>
    /// Asks a question, having first waited for the workspace to be ready to take one.
    ///
    /// **This is what the third test here was flaking on**, roughly one run in four, and the
    /// waiting was in the wrong place. Send is disabled while a turn is in flight, so asking
    /// a second question the moment the model had been *called* pressed a dead button: the
    /// question was never asked, the assertion waited ten seconds for an answer nobody had
    /// requested, and the failure pointed at the assertion rather than the press.
    ///
    /// Waiting for the model to be called is not the same as waiting for the turn to be
    /// done, and every other flake this suite has had was the same shape — an assertion
    /// racing a render. This one was a *press* racing a render, which is worse, because the
    /// thing that did not happen leaves nothing behind to look at.
    /// </summary>
    private static void Ask(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut, string question)
    {
        cut.Find("textarea.comp-input").Input(question);

        // The typing comes first and the waiting second, which is not a detail. Send is
        // disabled by an empty box as well as by a turn in flight, so waiting before typing
        // waits for something that can never happen — which is what the first attempt at
        // this fix did, and it turned one flaky test into three that failed every time.
        cut.WaitForAssertion(
            () => Assert.False(
                cut.Find("button.icon-btn-send").HasAttribute("disabled"),
                "the workspace is still busy with the last turn"),
            TimeSpan.FromSeconds(10));

        cut.Find("button.icon-btn-send").Click();
    }

    /// <summary>
    /// Waiting with room to spare.
    ///
    /// bUnit's default wait is a second, which is plenty on its own and not always plenty
    /// inside the full suite — a turn has to reach the store, the runtime and back while
    /// everything else in the run is competing for the machine. The third test here passed
    /// alone and failed in the suite exactly once, which is the shape this repository has
    /// recorded four times: **an assertion racing a render is a flake, and the fix is to wait
    /// for the thing rather than to hope.**
    /// </summary>
    private static void Eventually(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut, Action assertion)
        => cut.WaitForAssertion(assertion, TimeSpan.FromSeconds(10));

    [Fact]
    public void A_strip_a_tool_produced_reaches_a_model_that_can_look_at_it()
    {
        var model = new Watching(sees: true);
        var cut = RenderWith(model);

        AFilmstrip();
        Ask(cut, "What happens in the second shot?");

        // Asserted through WaitForAssertion rather than on the line after the click:
        // Blazor re-renders after the event and a loaded machine gets there second. Three
        // tests in this suite were flaky for exactly that.
        Eventually(cut, () =>
        {
            var carried = model.Saw.LastOrDefault(turn => turn.Images is { Count: > 0 });

            Assert.NotNull(carried);
            Assert.Equal("clip-frames.png", carried!.Images![0].FileName);
        });
    }

    /// <summary>
    /// The half that matters more. A model that cannot see is told a picture was produced
    /// rather than handed one it will ignore and then be asked about.
    /// </summary>
    [Fact]
    public void And_a_model_that_cannot_look_is_told_so_rather_than_handed_it()
    {
        var model = new Watching(sees: false);
        var cut = RenderWith(model);

        AFilmstrip();
        Ask(cut, "What happens in the second shot?");

        Eventually(cut, () =>
        {
            Assert.NotEmpty(model.Saw);
            Assert.DoesNotContain(model.Saw, turn => turn.Images is { Count: > 0 });

            Assert.Contains(
                model.Saw,
                turn => turn.Content.Contains("cannot look at pictures", StringComparison.Ordinal)
                        && turn.Content.Contains("clip-frames.png", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// Drained either way. A picture rides exactly one turn — a filmstrip from ten minutes
    /// ago silently attached to an unrelated question is worse than no filmstrip at all.
    /// </summary>
    [Fact]
    public void A_strip_rides_one_turn_and_no_more()
    {
        var model = new Watching(sees: true);
        var cut = RenderWith(model);

        AFilmstrip();
        Ask(cut, "What happens in the second shot?");

        Eventually(cut, () => Assert.Contains(model.Saw, turn => turn.Images is { Count: > 0 }));

        Ask(cut, "And what about the music?");

        Eventually(cut, () =>
        {
            Assert.Contains(model.Saw, turn => turn.Content.Contains("the music", StringComparison.Ordinal));
            Assert.DoesNotContain(model.Saw, turn => turn.Images is { Count: > 0 });
        });
    }
}
