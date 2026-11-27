using Bunit;
using Concierge.Shared.Components.Components;

namespace Concierge.Tests;

public sealed class WorkDestinationsTests : BunitContext
{
    [Fact]
    public void Saved_work_is_one_collapsed_sidebar_choice_on_first_visit()
    {
        var cut = Render<WorkDestinations>();

        var group = cut.Find("details.work-destinations");
        Assert.False(group.HasAttribute("open"));
        Assert.Equal("Your work", cut.Find("summary").TextContent.Trim());
        Assert.Equal(new[] { "/library", "/notebooks", "/scheduled" },
            cut.FindAll("nav a").Select(link => link.GetAttribute("href")));
    }
}
