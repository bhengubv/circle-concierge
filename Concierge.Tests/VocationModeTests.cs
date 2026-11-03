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
        Assert.Equal(5, Assert.Single(project.Workflows).Steps.Count);
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
        Assert.Contains("Not set up", page.Find(".project-vocation-tools").TextContent);
        await page.InvokeAsync(() => page.Find(".project-vocation button").Click());

        var saved = await projects.GetAsync(project.Id);
        var conversationId = Assert.Single(saved!.ConversationIds);
        Assert.NotNull(await Services.GetRequiredService<IConversationStore>().GetAsync(conversationId));
        Assert.EndsWith($"/chat/{conversationId}?project={project.Id}", Services.GetRequiredService<NavigationManager>().Uri);
    }
}
