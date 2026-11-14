using Bunit;
using Concierge.Shared.BookTrailers;
using Concierge.Shared.Design;
using Concierge.Shared.Media;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Concierge.Tests;

public sealed class BookTrailerPageTests : BunitContext
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "concierge-trailer-page", Guid.NewGuid().ToString("N"));
    private readonly RecordingSleptOnPackageFileSave _fileSave;
    private readonly FixedSleptOnReceiptPicker _receiptPicker = new();
    private readonly RecordingSleptOnPackagePicker _packagePicker = new();

    public BookTrailerPageTests()
    {
        _fileSave = new RecordingSleptOnPackageFileSave(Path.Combine(_root, "Files"));
        Services.AddSingleton(new BookTrailerLocalPaths(_root));
        Services.AddSingleton(new SleptOnMediaPackageWriter(Path.Combine(_root, "handoff")));
        Services.AddSingleton<ISleptOnPackageFileSave>(_fileSave);
        Services.AddSingleton<ISleptOnImportReceiptPicker>(_receiptPicker);
        Services.AddSingleton<ISleptOnPackagePicker>(_packagePicker);
    }

    [Fact]
    public async Task A_real_upload_reaches_the_importer_then_the_video_export_once_per_cut()
    {
        var exporter = new RecordingExport();
        Services.AddSingleton<IMediaExport>(exporter);
        var page = Render<Concierge.Shared.Components.Pages.BookTrailer>();

        Assert.Contains(".pdf,.epub,.txt", page.FindComponent<InputFile>().Find("input").GetAttribute("accept"));
        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(
            "The city is quiet. Mara finds a lantern. A signal appears. She follows the light. The door opens. The mystery begins.",
            "lantern.txt"));

        page.WaitForAssertion(() => Assert.Contains("lantern · TXT", page.Markup));
        Assert.Contains("I have permission", page.Markup);
        Assert.True(page.Find("button.btn-primary").HasAttribute("disabled"));
        page.FindAll("input[type=checkbox]")[1].Change(true);
        page.Find("button.btn-primary").Click();

        page.WaitForAssertion(() => Assert.Equal(1, exporter.Calls));
        page.WaitForAssertion(() => Assert.Contains("Book trailer preview", page.Markup));
        Assert.Equal(30, exporter.LastDocument!.Frames.Sum(frame => int.Parse(frame.Props["seconds"])));
        Assert.Single(page.FindAll("video"));
        Assert.Single(page.FindAll("a[download='trailer.mp4']"));
        page.Find("button[data-action=package]").Click();
        page.WaitForAssertion(() => Assert.Single(Directory.GetFiles(Path.Combine(_root, "handoff"), "*.zip")));
        Assert.Contains("SleptON! package", page.Markup);
        Assert.Contains("Save to Files", page.Markup);
        page.WaitForElement("button[data-action=save-package]").Click();
        page.WaitForAssertion(() => Assert.NotNull(_fileSave.SavedPath));
        Assert.True(File.Exists(_fileSave.SavedPath));
        Assert.Contains("Import from Concierge", page.Markup);
        _receiptPicker.RevisionId = await SleptOnPackageIdentity.ComputeAsync(
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "handoff"), "*.zip")));
        page.WaitForElement("button[data-action=read-receipt]").Click();
        page.WaitForAssertion(() => Assert.True(_receiptPicker.Called));
        Assert.Contains("SleptON! reports an imported draft", page.Markup);
        _receiptPicker.RevisionId = new string('b', 64);
        page.Find("button[data-action=read-receipt]").Click();
        page.WaitForAssertion(() => Assert.Contains("different SleptON! package", page.Markup));
        Assert.DoesNotContain("SleptON! reports an imported draft", page.Markup);

        page.Find("#trailer-mood").Change("hopeful");
        page.Find("button.btn-primary").Click();
        page.WaitForAssertion(() => Assert.Equal(2, exporter.Calls));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("video").Count));
        Assert.Equal("Fresh", exporter.LastDocument!.Look);
        Assert.Equal(2, Directory.GetDirectories(_root, "draft-*").Length);
    }

    [Theory]
    [InlineData("lantern.epub", "The Lantern · EPUB")]
    [InlineData("lantern.pdf", "lantern · PDF")]
    public void Pdf_and_epub_uploads_reach_the_same_local_import_flow(string name, string expected)
    {
        Services.AddSingleton<IMediaExport>(new RecordingExport());
        var page = Render<Concierge.Shared.Components.Pages.BookTrailer>();
        var bytes = name.EndsWith("epub", StringComparison.Ordinal)
            ? BookTrailerSourceAcceptanceTests.Epub()
            : BookTrailerSourceAcceptanceTests.Pdf("The lantern reveals the first clue.");

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, name));

        page.WaitForAssertion(() => Assert.Contains(expected, page.Markup));
        Assert.Empty(page.FindAll("[role=alert]"));
        Assert.True(page.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.True(File.Exists(Path.Combine(_root, "trailers.db")));
    }

    [Fact]
    public void Closing_and_reopening_keeps_the_draft_and_lets_the_person_continue()
    {
        Services.AddSingleton<IMediaExport>(new RecordingExport());
        var firstPage = Render<Concierge.Shared.Components.Pages.BookTrailer>();
        firstPage.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(
            "The city is quiet. Mara finds a lantern. A signal appears. She follows it.", "lantern.txt"));
        firstPage.WaitForAssertion(() => Assert.Contains("lantern · TXT", firstPage.Markup));
        firstPage.FindAll("input[type=checkbox]")[1].Change(true);
        firstPage.Find("button.btn-primary").Click();
        firstPage.WaitForAssertion(() => Assert.Single(firstPage.FindAll("video")));
        firstPage.Find("button[data-action=package]").Click();
        firstPage.WaitForAssertion(() => Assert.Single(Directory.GetFiles(Path.Combine(_root, "handoff"), "*.zip")));

        var reopened = Render<Concierge.Shared.Components.Pages.BookTrailer>();
        reopened.WaitForAssertion(() => Assert.Contains("Preview this cut", reopened.Markup));
        Assert.Contains("Earlier SleptON! packages", reopened.Markup);
        Assert.Single(reopened.FindAll("button[data-action=save-package]"));
        Assert.Single(reopened.FindAll("button[data-action=read-receipt]"));
        reopened.FindAll("button").Single(button => button.TextContent.Contains("Make another cut")).Click();
        reopened.WaitForAssertion(() => Assert.Contains("lantern · TXT", reopened.Markup));
        Assert.True(reopened.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [Fact]
    public void Web_host_does_not_offer_private_book_import_through_a_server_circuit()
    {
        Services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
        var page = Render<Concierge.Shared.Components.Pages.BookTrailer>();

        Assert.Contains("keep the book on your device", page.Markup);
        Assert.Empty(page.FindAll("input[type=file]"));
        Assert.Empty(page.FindAll("button[data-action=open-package]"));
    }

    [Fact]
    public async Task A_prepared_trailer_from_Files_reopens_without_the_source_book()
    {
        var fixture = Path.Combine(_root, "fixture");
        Directory.CreateDirectory(fixture);
        string Write(string name) { var path = Path.Combine(fixture, name); File.WriteAllText(path, name); return path; }
        var draft = new BookTrailerDraft(fixture, Write("video.mp4"), Write("source.json"),
            Write("captions.srt"), Write("script.json"), Write("credits.md"), new string('a', 64),
            "The Lantern", "source-excerpts");
        _packagePicker.SourcePath = (await new SleptOnMediaPackageWriter(Path.Combine(_root, "external"))
            .BookAsync(draft, true)).Path;
        var page = Render<Concierge.Shared.Components.Pages.BookTrailer>();

        page.Find("button[data-action=open-package]").Click();
        page.WaitForAssertion(() => Assert.Contains("Earlier SleptON! packages", page.Markup));
        Assert.Contains("The Lantern", page.Markup);
        Assert.Single(page.FindAll("button[data-action=read-receipt]"));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "handoff"), "*.zip"));
    }

    [Fact]
    public void Missing_encoder_explains_why_the_imported_book_cannot_be_rendered_here()
    {
        var page = Render<Concierge.Shared.Components.Pages.BookTrailer>();
        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(
            "The city is quiet. Mara finds a lantern. A signal appears.", "lantern.txt"));

        page.WaitForAssertion(() => Assert.Contains("Video export is unavailable", page.Markup));
        Assert.Empty(page.FindAll("button.btn-primary"));
    }

    [Fact]
    public void Invalid_book_explains_the_problem_and_does_not_offer_render()
    {
        Services.AddSingleton<IMediaExport>(new RecordingExport());
        var page = Render<Concierge.Shared.Components.Pages.BookTrailer>();
        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("This is not a book.", "book.docx"));

        page.WaitForAssertion(() => Assert.Contains("PDF, EPUB, or TXT", page.Find("[role=alert]").TextContent));
        Assert.Empty(page.FindAll("button.btn-primary"));
        Assert.Empty(page.FindAll("video"));
    }

    [Fact]
    public void Failed_replacement_import_keeps_the_previous_book_and_draft_available()
    {
        var exporter = new RecordingExport();
        Services.AddSingleton<IMediaExport>(exporter);
        var page = Render<Concierge.Shared.Components.Pages.BookTrailer>();
        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(
            "The city is quiet. Mara finds a lantern. A signal appears. She follows it.", "lantern.txt"));
        page.WaitForAssertion(() => Assert.Contains("lantern · TXT", page.Markup));
        page.FindAll("input[type=checkbox]")[1].Change(true);
        page.Find("button.btn-primary").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("video")));

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("Not a book", "wrong.docx"));

        page.WaitForAssertion(() => Assert.Contains("Could not read this book", page.Find("[role=alert]").TextContent));
        Assert.Contains("lantern · TXT", page.Markup);
        Assert.Single(page.FindAll("video"));
        Assert.Equal(1, exporter.Calls);
    }

    private sealed class RecordingExport : IMediaExport
    {
        public int Calls { get; private set; }
        public DesignDocument? LastDocument { get; private set; }
        public Task<ExportResult> VideoAsync(DesignDocument document, string outputPath, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastDocument = document;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, "video-fixture"u8.ToArray());
            return Task.FromResult(ExportResult.Made(outputPath));
        }
        public Task<ExportResult> SoundAsync(DesignDocument document, string outputPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Concierge.Web";
        public string WebRootPath { get; set; } = ".";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
