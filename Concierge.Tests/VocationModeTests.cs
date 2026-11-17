using Bunit;
using Concierge.Shared.Chat;
using Concierge.Shared.Components.Pages;
using Concierge.Shared.Planning;
using Concierge.Shared.Skills;
using Concierge.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class VocationModeTests : BunitContext
{
    [Fact]
    public void Social_media_project_brings_its_skill_into_its_turn_only()
    {
        var starter = Assert.Single(ProjectStarterTemplates.All, item => item.Id == VocationModes.SocialMediaManagerId);
        var project = starter.CreateProject();
        Assert.Equal(6, Assert.Single(project.Workflows).Steps.Count);
        Assert.Equal(VocationModes.SocialMediaManagerId, project.VocationModeId);
        Assert.Contains("marketing-growth", VocationModes.SkillIdsForProject(project, ["ui-ux-design"]));
        Assert.DoesNotContain("marketing-growth", VocationModes.SkillIdsForProject(null, ["ui-ux-design"]));

        Services.AddConciergeCore();
        var bundled = Assert.Single(Services.GetServices<ISkillSource>().OfType<BundledSkillSource>());
        Assert.Equal(new SkillCatalogService().GetSkills().Count, bundled.Discover().Count);
        Assert.Contains(bundled.Discover(), skill => skill.Id == "marketing-growth");
        Assert.NotNull(bundled.LoadBody(bundled.Discover().Single(skill => skill.Id == "marketing-growth")));
        var runtime = Services.GetRequiredService<ISkillRuntime>();
        var prompt = runtime.ComposeSystemPrompt(VocationModes.SkillIdsForProject(project, []));
        Assert.Contains("Create campaign assets", prompt);
    }

    [Fact]
    public async Task Focused_project_opens_a_linked_conversation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vocation-{Guid.NewGuid():N}", "concierge.db");
        Services.AddLogging();
        Services.AddConciergeChat(path);
        Services.AddSingleton<BackgroundRuns>();
        var projects = Services.GetRequiredService<IProjectStore>();
        var project = await projects.CreateAsync(VocationModes.Find(VocationModes.SocialMediaManagerId) is not null
            ? ProjectStarterTemplates.All.Single(item => item.Id == VocationModes.SocialMediaManagerId).CreateProject()
            : throw new InvalidOperationException());

        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects");
        var page = Render<Projects>();
        page.WaitForState(() => page.FindAll(".projects-page[aria-busy='false']").Count > 0);
        Assert.Contains("Social Media Manager", page.Find(".project-vocation").TextContent);
        Assert.Contains("Unavailable", page.Find(".project-vocation-tools").TextContent);
        await page.InvokeAsync(() => page.Find(".project-vocation button").Click());

        var saved = await projects.GetAsync(project.Id);
        var conversationId = Assert.Single(saved!.ConversationIds);
        Assert.NotNull(await Services.GetRequiredService<IConversationStore>().GetAsync(conversationId));
        Assert.EndsWith($"/chat/{conversationId}?project={project.Id}", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task Campaign_posts_survive_reopening_the_project_and_are_not_published()
    {
        var path = Path.Combine(Path.GetTempPath(), $"campaign-{Guid.NewGuid():N}", "concierge.db");
        Services.AddLogging();
        Services.AddConciergeChat(path);
        Services.AddSingleton<BackgroundRuns>();
        var projects = Services.GetRequiredService<IProjectStore>();
        var project = await projects.CreateAsync(ProjectStarterTemplates.All
            .Single(item => item.Id == VocationModes.SocialMediaManagerId).CreateProject());
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects");

        var page = Render<Projects>();
        page.WaitForState(() => page.FindAll(".projects-page[aria-busy='false']").Count > 0);
        await page.InvokeAsync(() => page.FindAll(".campaign-fields")[0].QuerySelectorAll("input")[0]
            .Change("Concierge + SleptON! launch"));
        await page.InvokeAsync(() => page.FindAll(".campaign-fields")[0].QuerySelectorAll("input")[1]
            .Change("Independent creators"));
        await page.InvokeAsync(() => page.FindAll(".campaign-fields")[1].QuerySelector("textarea")!
            .Change("Make it with Concierge. Review it in SleptON!."));
        await page.InvokeAsync(() => page.FindAll(".campaign-fields")[1].QuerySelector("button")!.Click());

        var saved = await projects.GetAsync(project.Id);
        var post = Assert.Single(saved!.SocialCampaign!.Posts);
        Assert.Equal("Independent creators", saved.SocialCampaign.Audience);
        Assert.Equal("Concierge", post.Brand);
        Assert.Null(post.MediaPath);
        Assert.Empty(page.FindAll(".campaign-send"));

        page.Dispose();
        var reopened = Render<Projects>();
        reopened.WaitForState(() => reopened.FindAll(".projects-page[aria-busy='false']").Count > 0);
        Assert.Contains(post.Copy, reopened.Find(".campaign-posts").TextContent);
    }
}
