using System.Runtime.CompilerServices;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Design;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// Opening the canvas shows the design that was on it.
///
/// **Found by looking at the running app, and it is the second defect in one afternoon where
/// two parts of the same screen disagreed.** The strip showed the restored design; the canvas
/// beside it said "Nothing here yet". Both render the same document through the same
/// renderer, so one of them had to be showing something stale — and it was the canvas.
///
/// The cause was an ordering that a comment defended: the restore was fire-and-forget so the
/// canvas "appears the instant it is asked for". It landed while the canvas iframe was still
/// loading its first, empty document; the in-flight load finished last and threw the restored
/// work away. The hesitation being avoided was a local file read. What it cost was every
/// design anybody had saved.
///
/// So this asserts on the canvas itself rather than on the strip, because the strip was right
/// the whole time.
///
/// **And these tests pass with the fix reverted, which is said here rather than left for
/// somebody to discover.** bUnit has no browser: there is no real iframe, no navigation, and
/// therefore no race to lose. What they pin is the contract — open the canvas, see the design
/// — and what actually proved the fix was the running desktop app, before and after. A test
/// that cannot fail for the reason it was written is worth keeping and worth labelling; it is
/// not worth trusting on its own.
/// </summary>
public sealed class CanvasRestoreTests : BunitContext
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

    /// <summary>A store holding one design, which takes a moment to hand it over.</summary>
    private sealed class Holding(DesignDocument? document, int afterMilliseconds = 0) : IDesignStore
    {
        public DesignDocument? Saved { get; private set; }

        public async Task<DesignRestore> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (afterMilliseconds > 0)
            {
                await Task.Delay(afterMilliseconds, cancellationToken);
            }

            return new DesignRestore(document, null);
        }

        public Task SaveAsync(DesignDocument document, CancellationToken cancellationToken = default)
        {
            Saved = document;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static DesignDocument ADesign()
    {
        var frame = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Sports Day"));

        return DesignDocument.Blank(medium: DesignMedium.Page)
            .Add(frame)
            .Add(DesignNode.New(DesignNodeKind.Heading, frame.Id, ("text", "Saturday the 14th")));
    }

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> Open(IDesignStore store)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"restore-{Guid.NewGuid():N}.db");
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

        // Replaces the file-backed one registered by AddConciergeTools, so this reads a
        // design nobody has to have saved and never touches the real one.
        Services.AddSingleton(store);

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
        cut.FindAll("button.ws-room-btn").First(room => room.TextContent.Contains("Creator")).Click();
        cut.WaitForState(() => cut.FindAll(".dz").Count > 0);

        return cut;
    }

    /// <summary>
    /// What the canvas frame is showing.
    ///
    /// `FindAll` rather than `Find`, so a frame that is not there yet comes back as an empty
    /// string and the surrounding `WaitForAssertion` simply waits — `Find` throws an
    /// ElementNotFoundException, which is not an assertion failure and may not be retried.
    ///
    /// **Said plainly: this is a plausible cause, not a diagnosed one.** These tests failed
    /// twice in about fifteen full-suite runs and passed alone every time, and neither
    /// failure message was captured before it stopped happening. Five consecutive green runs
    /// after this change prove nothing at that rate. If it returns, the message is the thing
    /// to get — this repository's history is four rounds of confident reasoning about timing
    /// that were all wrong until somebody measured.
    /// </summary>
    private static string Canvas(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
    {
        var frames = cut.FindAll("iframe.dz-frame");

        return frames.Count == 0 ? string.Empty : frames[0].GetAttribute("srcdoc") ?? string.Empty;
    }

    [Fact]
    public void The_canvas_shows_the_design_that_was_saved()
    {
        var cut = Open(new Holding(ADesign()));

        cut.WaitForAssertion(
            () => Assert.Contains("Saturday the 14th", Canvas(cut), StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The exact shape of the defect: the canvas saying there is nothing on it while the
    /// design is sitting right there.
    /// </summary>
    [Fact]
    public void And_never_says_there_is_nothing_on_it_when_there_is()
    {
        var cut = Open(new Holding(ADesign()));

        cut.WaitForAssertion(
            () => Assert.Contains("Saturday the 14th", Canvas(cut), StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("Nothing here yet", Canvas(cut), StringComparison.Ordinal);
    }

    /// <summary>
    /// A store that takes its time is the case the old ordering could not survive. The canvas
    /// waits for it rather than opening empty and hoping.
    /// </summary>
    [Fact]
    public void Even_when_the_design_takes_a_moment_to_come_back()
    {
        var cut = Open(new Holding(ADesign(), afterMilliseconds: 250));

        cut.WaitForAssertion(
            () => Assert.Contains("Saturday the 14th", Canvas(cut), StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Nothing saved is still a canvas, and it still says what to do. The empty state is
    /// right here — it was only ever wrong when there was something to show.
    /// </summary>
    [Fact]
    public void With_nothing_saved_the_canvas_is_empty_and_says_so()
    {
        var cut = Open(new Holding(null));

        cut.WaitForAssertion(
            () => Assert.Contains("Nothing here yet", Canvas(cut), StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// And the strip agrees with the canvas, which is the thing that was not true: two parts
    /// of one screen showing two different documents.
    /// </summary>
    [Fact]
    public void The_strip_and_the_canvas_show_the_same_design()
    {
        var cut = Open(new Holding(ADesign()));

        cut.WaitForAssertion(
            () => Assert.Contains("Saturday the 14th", Canvas(cut), StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        var moments = cut.FindAll(".dz-moment iframe");

        Assert.Contains(
            "Saturday the 14th",
            moments[^1].GetAttribute("srcdoc") ?? string.Empty,
            StringComparison.Ordinal);
    }
}
