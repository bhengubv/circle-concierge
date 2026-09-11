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

    private static void Ask(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut, string question)
    {
        cut.Find("textarea.comp-input").Input(question);
        cut.Find("button.icon-btn-send").Click();
    }

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
        cut.WaitForAssertion(() =>
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

        cut.WaitForAssertion(() =>
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

        cut.WaitForAssertion(() => Assert.Contains(model.Saw, turn => turn.Images is { Count: > 0 }));

        Ask(cut, "And what about the music?");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(model.Saw, turn => turn.Content.Contains("the music", StringComparison.Ordinal));
            Assert.DoesNotContain(model.Saw, turn => turn.Images is { Count: > 0 });
        });
    }
}
