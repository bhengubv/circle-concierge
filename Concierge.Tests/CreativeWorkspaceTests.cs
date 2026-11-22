using Bunit;
using Concierge.Shared.Chat;
using Concierge.Shared.Components.Pages;
using Concierge.Shared.Planning;
using Concierge.Shared.Tools;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class CreativeWorkspaceTests : BunitContext
{
    [Fact]
    public async Task EachProjectWorkspaceOpensItsOwnKind()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"concierge-workspaces-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(folder, "concierge.db"));
        var studio = new RecordingStudio();
        Services.AddSingleton<IDeveloperStudio>(studio);
        var project = await Services.GetRequiredService<IProjectStore>().CreateAsync(
            ConciergeProject.Create("First game", "Make a prototype"));

        foreach (var kind in ConciergeCodeWorkspaces.All)
        {
            var cut = Render<DeveloperStudio>(parameters => parameters
                .Add(page => page.ProjectId, project.Id)
                .Add(page => page.WorkspaceKind, kind.Id));
            cut.WaitForAssertion(() => Assert.Contains(kind.Title, cut.Find(".developer-studio-head h1").TextContent));
            await cut.InvokeAsync(() => cut.Find(".developer-studio-actions button").ClickAsync(new MouseEventArgs()));
            Assert.Equal(kind.Id, studio.LastKind);
        }
    }

    [Fact]
    public async Task UnknownWorkspaceCannotLaunchTheEditor()
    {
        // AddConciergeChat also creates projects.db beside the chat database.
        // A unique filename directly in the shared temp folder still collides.
        var folder = Path.Combine(Path.GetTempPath(), $"concierge-workspaces-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(folder, "concierge.db"));
        Services.AddSingleton<IDeveloperStudio>(new RecordingStudio());
        var project = await Services.GetRequiredService<IProjectStore>().CreateAsync(
            ConciergeProject.Create("Safe project", "Use known tools"));
        var cut = Render<DeveloperStudio>(parameters => parameters
            .Add(page => page.ProjectId, project.Id)
            .Add(page => page.WorkspaceKind, "untrusted"));
        cut.WaitForAssertion(() => Assert.Contains("This workspace is unavailable", cut.Markup));
        Assert.Empty(cut.FindAll(".developer-studio-actions button"));
    }

    [Fact]
    public async Task ProjectOffersEachWorkspaceWithAStableRoute()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"concierge-workspaces-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(folder, "concierge.db"));
        Services.AddSingleton<BackgroundRuns>();
        Services.AddSingleton<IDeveloperStudio>(new RecordingStudio());
        var project = await Services.GetRequiredService<IProjectStore>().CreateAsync(
            ConciergeProject.Create("Film", "Make a short movie"));
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/projects?project={project.Id}");

        var cut = Render<Projects>();
        cut.WaitForState(() => cut.FindAll(".projects-page[aria-busy='false']").Count > 0,
            TimeSpan.FromSeconds(15));
        var links = cut.FindAll(".project-creative-workspaces a");
        Assert.Equal(ConciergeCodeWorkspaces.All.Count, links.Count);
        foreach (var kind in ConciergeCodeWorkspaces.All)
            Assert.Contains(links, link => link.GetAttribute("href") == $"/projects/{project.Id}/studio/{kind.Id}");
    }

    [Fact]
    public async Task OlderHostCannotOpenAnUnsupportedCreativeWorkspace()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"concierge-workspaces-{Guid.NewGuid():N}");
        Services.AddLogging();
        Services.AddConciergeChat(Path.Combine(folder, "concierge.db"));
        var studio = new RecordingStudio { SupportedKinds = new HashSet<string> { "code" } };
        Services.AddSingleton<IDeveloperStudio>(studio);
        var project = await Services.GetRequiredService<IProjectStore>().CreateAsync(
            ConciergeProject.Create("Film", "Make a short movie"));

        var cut = Render<DeveloperStudio>(parameters => parameters
            .Add(page => page.ProjectId, project.Id)
            .Add(page => page.WorkspaceKind, "video"));
        cut.WaitForAssertion(() => Assert.True(cut.Find(".developer-studio-actions button").HasAttribute("disabled")));
        Assert.Contains("latest Concierge Code", cut.Markup);
        Assert.Null(studio.LastKind);
    }

    private sealed class RecordingStudio : IDeveloperStudio
    {
        public event EventHandler? Closed;
        public event EventHandler? AskCircleAiRequested;
        public string? LastKind { get; private set; }
        public IReadOnlySet<string> SupportedKinds { get; init; } = ConciergeCodeWorkspaces.All.Select(kind => kind.Id).ToHashSet();
        public Task<Uri> OpenAsync(Guid projectId, string projectName, CancellationToken cancellationToken = default)
            => OpenWorkspaceAsync(projectId, projectName, "code", cancellationToken);
        public Task<Uri> OpenWorkspaceAsync(Guid projectId, string projectName, string workspaceKind, CancellationToken cancellationToken = default)
        {
            LastKind = workspaceKind;
            return Task.FromResult(new Uri("http://127.0.0.1:12345/"));
        }
        public Task<IReadOnlySet<string>> GetInstalledExtensionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        public Task<IReadOnlySet<string>> GetSupportedWorkspaceKindsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(SupportedKinds);
        public Task InstallSetupAsync(string setupId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync() { Closed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    }
}
