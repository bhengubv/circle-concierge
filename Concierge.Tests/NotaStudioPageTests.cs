using Bunit;
using Concierge.Shared.Rpc.Mcp;
using Concierge.Shared.Media;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

[Collection("Nota studio")]
public sealed class NotaStudioPageTests : BunitContext
{
    [Fact]
    public async Task A_person_connects_exports_listens_and_reopens_the_saved_audio()
    {
        await using var server = await NotaStudioFlowTests.FakeNota.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "concierge-nota-page", Guid.NewGuid().ToString("N"));
        await using var studio = new NotaStudio(UnavailableToolApprovalService.Instance, root, server.Endpoint);
        Services.AddSingleton(studio);
        Services.AddSingleton(new SleptOnMediaPackageWriter(Path.Combine(root, "handoff")));
        var fileSave = new RecordingSleptOnPackageFileSave(Path.Combine(root, "Files"));
        Services.AddSingleton<ISleptOnPackageFileSave>(fileSave);
        var receiptPicker = new FixedSleptOnReceiptPicker();
        Services.AddSingleton<ISleptOnImportReceiptPicker>(receiptPicker);
        var page = Render<Concierge.Shared.Components.Pages.AudioStudio>();

        Assert.Contains("Connect to Nota", page.Markup);
        page.Find("button[data-action=connect]").Click();
        page.WaitForAssertion(() => Assert.True(page.Markup.Contains("Connected to Nota", StringComparison.Ordinal), page.Markup),
            TimeSpan.FromSeconds(15));
        page.Find("#audio-name").Change("My first song");
        page.Find("button[data-action=export]").Click();

        page.WaitForAssertion(() => Assert.True(page.FindAll("audio").Count == 1, page.Markup),
            TimeSpan.FromSeconds(15));
        Assert.DoesNotContain("This draft is silent", page.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("My first song", page.Find("section[aria-label='Audio draft'] h2").TextContent);
        Assert.True(File.Exists(Assert.Single(Directory.GetFiles(root, "*.wav"))));
        page.Find("input[data-action=share-rights]").Change(true);
        page.Find("button[data-action=package]").Click();
        page.WaitForAssertion(() => Assert.Single(Directory.GetFiles(Path.Combine(root, "handoff"), "*.zip")));
        page.WaitForElement("button[data-action=save-package]").Click();
        page.WaitForAssertion(() => Assert.NotNull(fileSave.SavedPath));
        Assert.True(File.Exists(fileSave.SavedPath));
        Assert.Contains("Import from Concierge", page.Markup);
        receiptPicker.RevisionId = await SleptOnPackageIdentity.ComputeAsync(
            Assert.Single(Directory.GetFiles(Path.Combine(root, "handoff"), "*.zip")));
        page.WaitForElement("button[data-action=read-receipt]").Click();
        page.WaitForAssertion(() => Assert.True(receiptPicker.Called));
        Assert.Contains("SleptON! reports an imported draft", page.Markup);

        var reopened = Render<Concierge.Shared.Components.Pages.AudioStudio>();
        reopened.WaitForAssertion(() => Assert.Contains("My first song", reopened.Markup));
        Assert.Contains("Play this draft", reopened.Markup);
        Assert.Contains("Earlier SleptON! packages", reopened.Markup);
        Assert.Single(reopened.FindAll("button[data-action=save-package]"));
        Assert.Single(reopened.FindAll("button[data-action=read-receipt]"));
    }

    [Fact]
    public void Without_a_desktop_nota_connection_the_page_explains_the_next_step()
    {
        var page = Render<Concierge.Shared.Components.Pages.AudioStudio>();
        Assert.Contains("Desktop", page.Markup);
        Assert.Empty(page.FindAll("button[data-action=export]"));
    }

    [Fact]
    public async Task Phone_can_open_a_prepared_audio_package_from_Files_without_Nota()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-audio-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var audio = Path.Combine(root, "song.wav");
        await File.WriteAllTextAsync(audio, "audio fixture");
        var package = await new SleptOnMediaPackageWriter(Path.Combine(root, "external"))
            .AudioAsync(new NotaAudioDraft(audio, "wav", 13, true), true);
        var picker = new RecordingSleptOnPackagePicker { SourcePath = package.Path };
        Services.AddSingleton<ISleptOnPackagePicker>(picker);
        Services.AddSingleton(new SleptOnMediaPackageWriter(Path.Combine(root, "handoff")));
        Services.AddSingleton<ISleptOnPackageFileSave>(new RecordingSleptOnPackageFileSave(Path.Combine(root, "Files")));
        Services.AddSingleton<ISleptOnImportReceiptPicker>(new FixedSleptOnReceiptPicker());
        var page = Render<Concierge.Shared.Components.Pages.AudioStudio>();

        Assert.Empty(page.FindAll("button[data-action=export]"));
        page.Find("button[data-action=open-package]").Click();
        page.WaitForAssertion(() => Assert.Contains("Earlier SleptON! packages", page.Markup));
        Assert.Contains("song", page.Markup);
        Assert.Single(page.FindAll("button[data-action=save-package]"));
        Assert.Single(page.FindAll("button[data-action=read-receipt]"));
    }
}
