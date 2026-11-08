using Bunit;
using Concierge.Shared.Components.Components;
using Concierge.Shared.Components.Pages;
using Concierge.Shared.Rpc.Mcp;
using Concierge.Shared.Components.State;
using Fluxor;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Concierge.Ui.NUnitTests;

[TestFixture]
public sealed class HarnessUiTests
{
    [Test]
    public void Workload_chooser_enables_real_skills_and_opens_the_matching_project_starter()
    {
        using var context = new BunitContext();
        var enabled = new List<string>();
        var conversationId = Guid.NewGuid();
        var rendered = context.Render<ToolsAndFeatures>(parameters => parameters
            .Add(component => component.ActiveIds, Array.Empty<string>())
            .Add(component => component.ConversationId, conversationId)
            .Add(component => component.OnToggleSkill, id => enabled.Add(id)));

        var social = rendered.FindAll(".feature-card").First(card => card.TextContent.Contains("Social Media Manager"));
        social.QuerySelector("button")!.Click();

        Assert.That(enabled, Is.EquivalentTo(new[] { "marketing-growth", "media-publishing-content-operations" }));
        Assert.That(social.QuerySelector("a")!.GetAttribute("href"), Is.EqualTo($"/projects?starter=social-media-manager&conversation={conversationId}"));
        Assert.That(rendered.Markup, Does.Contain("Desktop only"));
    }

    [Test]
    public void Tool_chooser_shows_configured_tool_without_exposing_its_command()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-tools-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "[{\"name\":\"Phoneuse\",\"command\":\"C:/private/phoneuse-mcp.exe\",\"enabled\":false}]");
            using var context = new BunitContext();
            context.Services.AddSingleton(new McpServerOptions(path));
            var rendered = context.Render<ToolsAndFeatures>();

            Assert.That(rendered.Markup, Does.Contain("Phoneuse"));
            Assert.That(rendered.Markup, Does.Contain("Turned off"));
            Assert.That(rendered.Markup, Does.Not.Contain("C:/private/phoneuse-mcp.exe"));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Running_conversations_are_projected_into_the_shared_Fluxor_store()
    {
        using var services = new ServiceCollection()
            .AddFluxor(options => options.ScanAssemblies(typeof(RunningConversationsState).Assembly))
            .BuildServiceProvider();
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStore>();
        await store.InitializeAsync();
        var state = scope.ServiceProvider.GetRequiredService<IState<RunningConversationsState>>();
        var first = Guid.NewGuid();
        scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Dispatch(new RunningConversationsRefreshed([first, first]));

        Assert.That(state.Value.ConversationIds, Is.EquivalentTo(new[] { first }));
    }

    [Test]
    public void Bunit_renders_the_shared_welcome_component_for_both_MAUI_heads()
    {
        using var context = new BunitContext();
        var rendered = context.Render<WelcomePanel>();

        Assert.That(rendered.Find("#welcome-title").TextContent, Is.EqualTo("What would help today?"));
        Assert.That(rendered.Find(".welcome-brand").TextContent, Does.Contain("Concierge"));
    }
}
