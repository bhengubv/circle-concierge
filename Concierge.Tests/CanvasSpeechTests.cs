using System.Runtime.CompilerServices;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// Saying something to the canvas, in each medium.
///
/// **This exists because of a question I could not answer by looking.** Driving the running
/// desktop app, a sentence typed into the composer with Space open never applied — and the
/// same synthetic clicks were also being ignored by the Windows maximize button, which is not
/// Blazor at all. So the evidence was worthless in both directions: it did not show the app
/// was broken and it did not show it was fine.
///
/// This is the question asked where the answer is trustworthy. One test per medium, each
/// typing a sentence that medium understands and asserting the canvas actually changed.
/// </summary>
public sealed class CanvasSpeechTests : BunitContext
{
    private sealed class ReadyRuntime : IChatRuntime
    {
        public string Id => "test";
        public string EngineLabel => "Test-Engine (stub)";
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

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> Open()
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
        Services.AddSingleton<IEnumerable<IChatRuntime>>(_ => new IChatRuntime[] { new ReadyRuntime() });
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

        cut.FindAll("button.ws-head")
            .First(head => head.QuerySelector(".ws-head-name")!.TextContent.Trim() == "Tools")
            .Click();

        cut.WaitForState(() => cut.FindAll("button.ws-room-btn").Count > 0);
        cut.FindAll("button.ws-room-btn").First(room => room.TextContent.Contains("Design")).Click();
        cut.WaitForState(() => cut.FindAll(".dz").Count > 0);

        return cut;
    }

    private static void Making(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut, string medium)
    {
        cut.FindAll("button.dz-look")
            .First(button => button.TextContent.Trim().EndsWith(medium, StringComparison.Ordinal))
            .Click();

        cut.WaitForAssertion(
            () => Assert.Contains(
                "is-on",
                cut.FindAll("button.dz-look")
                    .First(b => b.TextContent.Trim().EndsWith(medium, StringComparison.Ordinal))
                    .GetAttribute("class") ?? string.Empty,
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
    }

    private static void Say(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut, string words)
    {
        cut.Find("textarea.comp-input").Input(words);
        cut.Find("button.icon-btn-send").Click();
    }

    /// <summary>
    /// One sentence per medium, in the words that medium's own invitation asks for.
    ///
    /// The canvas keeps every state, so a sentence that was understood adds a moment to the
    /// strip. That is the thing to assert: not what was drawn, but that anything happened at
    /// all — which is precisely what looked broken on the running app.
    /// </summary>
    [Theory]
    [InlineData("Page", "add a title that says Sports Day")]
    [InlineData("Slides", "add a slide")]
    [InlineData("Video", "add a shot")]
    [InlineData("Space", "add a box")]
    [InlineData("Sound", "add a track")]
    [InlineData("Phone", "add a screen")]
    [InlineData("Board", "add a panel")]
    public void A_sentence_is_applied_in_every_medium(string medium, string words)
    {
        var cut = Open();

        Making(cut, medium);

        var before = cut.FindAll(".dz-moment").Count;

        Say(cut, words);

        cut.WaitForAssertion(
            () => Assert.True(
                cut.FindAll(".dz-moment").Count > before,
                $"\"{words}\" changed nothing while making {medium}."),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// And the composer is emptied when it was understood, because words left in the box
    /// after something worked read as something that did not.
    /// </summary>
    [Fact]
    public void And_the_words_leave_the_box_when_they_were_understood()
    {
        var cut = Open();

        Making(cut, "Space");
        Say(cut, "add a box");

        cut.WaitForAssertion(
            () => Assert.True(
                string.IsNullOrEmpty(cut.Find("textarea.comp-input").GetAttribute("value")),
                "the words stayed in the box"),
            TimeSpan.FromSeconds(10));
    }
}
