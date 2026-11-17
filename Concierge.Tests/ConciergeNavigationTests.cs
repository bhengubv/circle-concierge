using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Planning;
using Concierge.Shared.Media;
using Concierge.Shared.Components.Layout;
using Concierge.Shared.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ConciergeNavigationTests : BunitContext
{
    [Fact]
    public async Task Shell_exposes_five_destinations_and_the_ten_most_recent_projects()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        Services.AddConciergeChat(Path.Combine(Path.GetTempPath(), $"nav-{Guid.NewGuid():N}.db"));
        JSInterop.Mode = JSRuntimeMode.Loose;
        var projects = Services.GetRequiredService<IProjectStore>();
        for (var index = 0; index < 11; index++)
            await projects.CreateAsync(ConciergeProject.Create($"Campaign {index}", "Prepare a private campaign"));

        var shell = Render<ShellLayout>(parameters => parameters.Add(layout => layout.Body,
            builder => builder.AddContent(0, "Page content")));

        shell.WaitForState(() => shell.FindAll(".cm-project-switcher-list a[href^='/projects?project=']").Count == 10);
        Assert.Equal(new[] { "Home", "Chat", "B!", "Studio", "You" },
            shell.FindAll(".cm-bottom-nav > *").Select(item => item.TextContent.Trim()).ToArray());
        Assert.Contains("Campaign 10", shell.Find(".cm-project-switcher-list").TextContent);
        Assert.DoesNotContain("Campaign 0", shell.Find(".cm-project-switcher-list").TextContent);

        await shell.InvokeAsync(() => shell.Find(".cm-bottom-b").Click());
        Assert.Equal("http://localhost/", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Contains("Listening", shell.Find(".cm-voice-sheet").TextContent);
    }

    [Fact]
    public void Home_invites_work_without_a_restored_conversation()
    {
        var home = Render<HomeLanding>();
        Assert.Equal(new[] { "Chats", "Notes", "Studio", "Library", "Tutorials", "Demos" },
            home.FindAll(".cm-home-tile strong").Select(title => title.TextContent.Trim()).ToArray());
        Assert.Equal(new[] { "/chat", "/notes", "/studio", "/library", "/tutorials", "/demos" },
            home.FindAll(".cm-home-tile").Select(tile => tile.GetAttribute("href")).ToArray());
    }

    [Fact]
    public void You_keeps_saved_work_and_approvals_accessible()
    {
        var you = Render<You>();
        Assert.Equal(new[] { "/library", "/notebooks", "/scheduled", "/approvals" },
            you.FindAll(".cm-you-links a").Select(link => link.GetAttribute("href")).ToArray());
    }

    [Fact]
    public async Task Studio_groups_projects_without_an_endless_open_list()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        Services.AddConciergeChat(Path.Combine(Path.GetTempPath(), $"studio-{Guid.NewGuid():N}.db"));
        var projects = Services.GetRequiredService<IProjectStore>();
        await projects.CreateAsync(ConciergeProject.Create("Company site", "Launch a site", "Business"));
        await projects.CreateAsync(ConciergeProject.Create("Family garden", "Grow food", "Personal & creative"));

        var studio = Render<StudioLanding>();

        studio.WaitForState(() => studio.FindAll(".cm-studio-group").Count == 2);
        Assert.Equal(new[] { "Work", "Home & personal" },
            studio.FindAll(".cm-studio-group summary span").Select(item => item.TextContent.Trim()).ToArray());
        Assert.All(studio.FindAll(".cm-studio-group"), group => Assert.False(group.HasAttribute("open")));
        Assert.Contains(studio.FindAll(".cm-studio-project-list a"), item => item.GetAttribute("href")?.StartsWith("/projects?project=") == true);
    }

    [Fact]
    public void Notes_saves_a_typed_note_on_this_device()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notes-{Guid.NewGuid():N}.json");
        var store = new FileKnowledgeNotebookStore(path);
        Services.AddSingleton<IKnowledgeNotebookStore>(store);
        var notes = Render<Notes>();

        notes.Find("#cm-note-title").Change("Meeting idea");
        notes.Find("#cm-note-body").Change("Make the agenda shorter.");
        notes.Find(".cm-note-actions button").Click();

        Assert.Contains("Saved on this device.", notes.Markup);
        Assert.Equal("Make the agenda shorter.", Assert.Single(Assert.Single(store.All()).Sources).Text);
    }

    [Fact]
    public async Task B_voice_keeps_the_current_screen_until_the_person_uses_the_transcript()
    {
        Services.AddLogging();
        Services.AddConciergeCore();
        Services.AddConciergeChat(Path.Combine(Path.GetTempPath(), $"voice-nav-{Guid.NewGuid():N}.db"));
        Services.AddSingleton<IVoiceRuntime>(new TestEars());
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("conciergeVoice.stop", "shell").SetResult(Convert.ToBase64String([1, 2, 3]));
        JSInterop.Setup<bool>("conciergeVoice.hasTextTarget").SetResult(false);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/studio");
        var shell = Render<ShellLayout>(parameters => parameters.Add(layout => layout.Body,
            builder => builder.AddContent(0, "Studio content")));

        await shell.InvokeAsync(() => shell.Find(".cm-bottom-b").Click());
        Assert.EndsWith("/studio", nav.Uri);
        await shell.InvokeAsync(() => shell.Find(".cm-bottom-b").Click());

        shell.WaitForState(() => shell.FindAll("#cm-voice-transcript").Count == 1);
        Assert.EndsWith("/studio", nav.Uri);
        await shell.InvokeAsync(() => shell.Find(".cm-voice-actions button").Click());
        Assert.Contains("/chat?draft=1&request=", nav.Uri);
    }

    private sealed class TestEars : IVoiceRuntime
    {
        public string Id => "circleai-service-test";
        public string EngineLabel => "Test ears";
        public bool IsReady => true;
        public string StatusMessage => "Ready";
        public bool SupportsTranscription => true;
        public bool SupportsSynthesis => false;
        public Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionResult(Id, "Create a project", "en", null));
        public Task<SpeechResult> SynthesizeAsync(string text, string voice = "alloy",
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
