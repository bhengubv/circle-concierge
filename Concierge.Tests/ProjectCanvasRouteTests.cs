using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Design;
using Concierge.Shared.Planning;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ProjectCanvasRouteTests : BunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectSlidesLinkOpensTheDeckInsteadOfPlainChat(bool handheld)
    {
        var root = Path.Combine(Path.GetTempPath(), $"concierge-slides-route-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeCore();
        Services.AddSingleton(_ => new Concierge.Shared.Skills.UserSkillFolders(Path.Combine(root, "skills.json")));
        Services.AddSingleton<Concierge.Shared.Session.ISessionState>(
            new Concierge.Shared.Session.FileSessionState(Path.Combine(root, "session.json")));
        Services.AddConciergeChat(Path.Combine(root, "concierge.db"));
        Services.AddSingleton<IEnumerable<IChatRuntime>>(_ => Array.Empty<IChatRuntime>());
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeTools(Services);
        Services.AddConciergeDesignTools();
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeRuntime(Services);
        Concierge.Shared.Diagrams.ConciergeDiagramsServiceCollectionExtensions.AddConciergeDiagrams(Services);
        Services.AddSingleton<IEnumerable<Concierge.Shared.Media.IImageRuntime>>(
            _ => Array.Empty<Concierge.Shared.Media.IImageRuntime>());
        var designs = new ProjectDesignStore(root);
        Services.AddSingleton(designs);
        JSInterop.Mode = JSRuntimeMode.Loose;

        using (var db = Services.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>().CreateDbContext())
            db.Database.EnsureCreated();

        var conversation = await Services.GetRequiredService<IConversationStore>()
            .StartAsync("local", "Pitch slides");
        var project = await Services.GetRequiredService<IProjectStore>()
            .CreateAsync(ConciergeProject.Create("Pitch", "Explain the idea") with
            {
                ConversationIds = [conversation.Id],
                SlidesConversationId = conversation.Id,
            });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/chat/{conversation.Id}?project={project.Id}&canvas=slides");

        if (handheld)
        {
            var cut = Render<Concierge.Shared.Components.Workspace.Handheld.Workspace>(
                parameters => parameters.Add(component => component.ConversationId, conversation.Id));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".dz")), TimeSpan.FromSeconds(10));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ws[aria-busy='false']")), TimeSpan.FromSeconds(10));
            Assert.Contains("slide", cut.Find("iframe.dz-frame").GetAttribute("srcdoc") ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["PPTX", "PDF"], cut.FindAll(".dz-export").Select(button => button.TextContent.Trim()).ToArray());
            Assert.All(cut.FindAll(".dz-export"), button => Assert.False(button.HasAttribute("disabled")));
            await cut.InvokeAsync(() => cut.Find("textarea.comp-input").Input(
                "Add a slide titled Project status with three points: goal, current work, next step."));
            await cut.InvokeAsync(() => cut.Find("button.icon-btn-send").Click());
            var path = ((FileDesignStore)designs.For(project.Id)).Path;
            cut.WaitForAssertion(() => Assert.True(File.Exists(path)), TimeSpan.FromSeconds(10));
            var saved = await designs.For(project.Id).LoadSessionAsync();
            Assert.Null(saved.Problem);
            var document = new DesignSession(saved.Session!).Current;
            var slide = Assert.Single(document.Frames);
            Assert.Equal("Project status", slide.Text);
            Assert.Equal(new[] { "goal", "current work", "next step" },
                document.ChildrenOf(slide.Id).Select(point => point.Text));
            Assert.Equal(string.Empty, (await Services.GetRequiredService<Concierge.Shared.Session.ISessionState>()
                .LoadAsync()).UnsentFor(conversation.Id));
        }
        else
        {
            var cut = Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>(
                parameters => parameters.Add(component => component.ConversationId, conversation.Id));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".dz")), TimeSpan.FromSeconds(10));
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ws[aria-busy='false']")), TimeSpan.FromSeconds(10));
            Assert.Contains("slide", cut.Find("iframe.dz-frame").GetAttribute("srcdoc") ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["PPTX", "PDF"], cut.FindAll(".dz-export").Select(button => button.TextContent.Trim()).ToArray());
            Assert.All(cut.FindAll(".dz-export"), button => Assert.False(button.HasAttribute("disabled")));
            await cut.InvokeAsync(() => cut.Find("textarea.comp-input").Input(
                "Add a slide titled Project status with three points: goal, current work, next step."));
            await cut.InvokeAsync(() => cut.Find("button.icon-btn-send").Click());
            var saved = await designs.For(project.Id).LoadSessionAsync();
            Assert.Null(saved.Problem);
            Assert.Single(new DesignSession(saved.Session!).Current.Frames);
            Assert.Equal(string.Empty, (await Services.GetRequiredService<Concierge.Shared.Session.ISessionState>()
                .LoadAsync()).UnsentFor(conversation.Id));
        }
    }
}
