using Concierge.Shared.Design;
using Concierge.Shared.Planning;

namespace Concierge.Tests;

public sealed class ProjectSlidesTests
{
    [Fact]
    public void PresentationStarterKeepsReviewBeforeExport()
    {
        var starter = Assert.Single(ProjectStarterTemplates.All, item => item.Id == "presentation");
        var workflow = Assert.Single(starter.CreateProject().Workflows);
        var steps = workflow.Steps.Select(step => step.Name).ToArray();

        Assert.Contains(steps, step => step.Contains("slides", StringComparison.OrdinalIgnoreCase));
        Assert.True(Array.FindIndex(steps, step => step.Contains("Review", StringComparison.OrdinalIgnoreCase))
            < Array.FindIndex(steps, step => step.Contains("Export", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ProjectSlidesKeepTheirOwnHistoryAcrossReopening()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-project-slides-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var stores = new ProjectDesignStore(root);
            var first = new DesignSession(medium: DesignMedium.Deck);
            var slide = DesignNode.New(DesignNodeKind.Frame, null, ("text", "First Project"));
            first.Record(first.Current.Add(slide), "Added a slide");
            await stores.For(firstId).SaveSessionAsync(first.Capture());

            Assert.Null((await stores.For(secondId).LoadSessionAsync()).Session);
            var reopened = new ProjectDesignStore(root);
            var saved = await reopened.For(firstId).LoadSessionAsync();
            Assert.Null(saved.Problem);
            Assert.Equal(2, saved.Session!.Moments.Count);
            Assert.Equal("First Project", new DesignSession(saved.Session).Current.Frames.Single().Text);
            Assert.Equal(DesignMedium.Deck, new DesignSession(saved.Session).Current.Medium);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
