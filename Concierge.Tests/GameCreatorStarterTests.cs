using Concierge.Shared.Planning;

namespace Concierge.Tests;

public sealed class GameCreatorStarterTests
{
    [Fact]
    public void GameCreatorStartsWithARealMobilePlaytestBeforeRelease()
    {
        var starter = Assert.Single(ProjectStarterTemplates.Games);
        var project = starter.CreateProject();
        var workflow = Assert.Single(project.Workflows);
        var steps = workflow.Steps.Select(step => step.Name).ToArray();

        Assert.Equal("game-creator", starter.Id);
        Assert.Equal("First playable game", workflow.Name);
        Assert.Contains(steps, step => step.Contains("playable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(steps, step => step.Contains("phone", StringComparison.OrdinalIgnoreCase));
        Assert.True(Array.FindIndex(steps, step => step.Contains("phone", StringComparison.OrdinalIgnoreCase))
                    < Array.FindIndex(steps, step => step.Contains("release", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("original", workflow.Steps[0].Description, StringComparison.OrdinalIgnoreCase);
    }
}
