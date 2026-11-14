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
        page.WaitForElement("button[data-action=read-receipt]").Click();
        page.WaitForAssertion(() => Assert.True(receiptPicker.Called));
        Assert.Contains("SleptON! reports an imported draft", page.Markup);

        var reopened = Render<Concierge.Shared.Components.Pages.AudioStudio>();
        reopened.WaitForAssertion(() => Assert.Contains("My first song", reopened.Markup));
        Assert.Contains("Play this draft", reopened.Markup);
    }

    [Fact]
    public void Without_a_desktop_nota_connection_the_page_explains_the_next_step()
    {
        var page = Render<Concierge.Shared.Components.Pages.AudioStudio>();
        Assert.Contains("Desktop", page.Markup);
        Assert.Empty(page.FindAll("button[data-action=export]"));
    }
}
