using Bunit;
using Microsoft.AspNetCore.Components;

namespace Concierge.Tests;

public sealed class ModelSetupTutorialTests : BunitContext
{
    [Fact]
    public void Active_preparation_keeps_tutorial_steps_visible_beside_progress()
    {
        var cut = Render<Concierge.Shared.Components.Components.ModelSetupTutorial>(parameters => parameters
            .Add(tutorial => tutorial.Show, true)
            .Add(tutorial => tutorial.Busy, true));

        Assert.Contains("STEP 1 OF 3", cut.Markup);
        Assert.Contains("smallest upward", cut.Find(".model-setup-tutorial-staging").TextContent);
        Assert.Contains("Try a sample request", cut.Markup);
    }

    [Fact]
    public void Tutorial_is_not_rendered_when_setup_is_not_relevant()
    {
        var cut = Render<Concierge.Shared.Components.Components.ModelSetupTutorial>(parameters => parameters
            .Add(tutorial => tutorial.Show, false)
            .Add(tutorial => tutorial.Busy, false));

        Assert.Empty(cut.FindAll(".model-setup-tutorial"));
    }

    [Fact]
    public void Tutorial_can_be_hidden_and_resumed_without_hiding_active_preparation()
    {
        var cut = Render<Concierge.Shared.Components.Components.ModelSetupTutorial>(parameters => parameters
            .Add(tutorial => tutorial.Show, true)
            .Add(tutorial => tutorial.Busy, true));

        cut.Find("button.model-setup-tutorial-toggle").Click();

        Assert.Empty(cut.FindAll(".model-setup-tutorial-step"));
        Assert.Contains("Your setup and conversation can continue", cut.Markup);
        Assert.Contains("is-busy", cut.Find(".model-setup-tutorial").ClassName);

        cut.Find("button.model-setup-tutorial-toggle").Click();

        Assert.Contains("STEP 1 OF 3", cut.Markup);
        Assert.Contains("Start with the outcome", cut.Find(".model-setup-tutorial").TextContent);
    }

    [Fact]
    public void Tutorial_moves_through_three_steps_and_finishes_by_saving_its_place()
    {
        var step = 0;
        var collapsed = false;
        var cut = Render<Concierge.Shared.Components.Components.ModelSetupTutorial>(parameters => parameters
            .Add(tutorial => tutorial.Show, true)
            .Add(tutorial => tutorial.Busy, true)
            .Add(tutorial => tutorial.Step, step)
            .Add(tutorial => tutorial.Collapsed, collapsed)
            .Add(tutorial => tutorial.OnStepChanged, EventCallback.Factory.Create<int>(this, value =>
            {
                step = value;
            }))
            .Add(tutorial => tutorial.OnCollapsedChanged, EventCallback.Factory.Create<bool>(this, value =>
            {
                collapsed = value;
            })));

        cut.Find(".model-setup-tutorial-navigation button:last-child").Click();
        Assert.Contains("STEP 2 OF 3", cut.Markup);
        Assert.Contains("Decide what Concierge may do", cut.Find(".model-setup-tutorial").TextContent);

        cut.Find(".model-setup-tutorial-navigation button:last-child").Click();
        Assert.Contains("STEP 3 OF 3", cut.Markup);
        Assert.Contains("Keep working across devices", cut.Find(".model-setup-tutorial").TextContent);

        cut.Find(".model-setup-tutorial-navigation button:last-child").Click();
        Assert.True(collapsed);
        Assert.Contains("Resume tutorial", cut.Markup);
    }
}
