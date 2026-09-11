using System.Runtime.CompilerServices;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// What the canvas says when the model answers it.
///
/// **Found by typing "add a wall" into a room and watching nothing happen.** Nothing is the
/// right word: no wall, no new moment, no error, no sign a turn had even run. "add a sphere"
/// works instantly, so the local path was fine — "wall" is simply not in the canvas
/// vocabulary, and the sentence fell through to the model exactly as designed.
///
/// The model answered with nothing at all, and the thread behind the canvas held "YOU: add a
/// wall" followed by an empty Concierge turn. None of it was visible, because with the canvas
/// open the middle of the screen is the canvas and there is no thread on it. Even the
/// "thinking" indicator was replaced by the name of the last design moment, so the screen
/// looked idle while a turn was running.
///
/// A surface whose whole promise is "say what you want and look" cannot be the one surface
/// that cannot report failure.
/// </summary>
public sealed class CanvasAnswersTests : BunitContext
{
    private sealed class Says(string reply) : IChatRuntime
    {
        public string Id => "says";
        public string EngineLabel => "Says (stub)";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;

            if (reply.Length > 0)
            {
                yield return reply;
            }
        }
    }

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> Canvas(
        IChatRuntime runtime)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"canvas-{Guid.NewGuid():N}.db");
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

        var cut = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();
        cut.WaitForState(() => cut.FindAll("section.ws-group").Count > 0);

        // Open the canvas the way a person does — and the Tools group is folded, so it has to
        // be unfolded first. Worth the two lines: a test that reaches the button some other
        // way would pass on a build where nothing could open the canvas at all, which is a
        // mistake this repository has already made once.
        cut.FindAll("button.ws-head")
            .Single(head => head.TextContent.Contains("Tools", StringComparison.Ordinal))
            .Click();

        cut.WaitForState(() => cut.FindAll("button.ws-room-btn").Count > 0);

        cut.FindAll("button.ws-room-btn")
            .Single(button => button.TextContent.Contains("Design", StringComparison.Ordinal))
            .Click();

        cut.WaitForState(() => cut.FindAll(".dz").Count > 0);

        return cut;
    }

    private static void Say(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut, string words)
    {
        cut.Find("textarea.comp-input").Input(words);
        cut.WaitForAssertion(
            () => Assert.False(cut.Find("button.icon-btn-send").HasAttribute("disabled")),
            TimeSpan.FromSeconds(10));
        cut.Find("button.icon-btn-send").Click();
    }

    /// <summary>
    /// A sentence the canvas cannot apply, answered in words by the model, is shown.
    /// </summary>
    [Fact]
    public void What_the_model_says_about_a_design_reaches_the_canvas()
    {
        var cut = Canvas(new Says("I cannot add a wall to this room."));

        Say(cut, "add a wall");

        cut.WaitForAssertion(
            () => Assert.Contains("I cannot add a wall", cut.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// And a model that says nothing is reported as having said nothing — which is the case
    /// that was actually happening, and the one that looked exactly like success.
    /// </summary>
    [Fact]
    public void And_a_model_that_answers_with_nothing_says_so_rather_than_looking_like_success()
    {
        var cut = Canvas(new Says(string.Empty));

        Say(cut, "add a wall");

        cut.WaitForAssertion(
            () => Assert.Contains("had no answer for that", cut.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// An ordinary sentence is still applied locally and never reaches the model, so the
    /// canvas keeps feeling like a pen. Without this the fix above could have been "send
    /// everything to the model", which would ruin the surface to fix a message.
    /// </summary>
    [Fact]
    public void And_a_sentence_the_canvas_knows_is_still_answered_without_a_model()
    {
        var cut = Canvas(new Says("this should never be reached"));

        Say(cut, "add a heading saying Hello");

        cut.WaitForAssertion(
            () => Assert.DoesNotContain("this should never be reached", cut.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
    }
}
