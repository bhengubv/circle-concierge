using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Concierge.Shared.Design;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Concierge.Tests;

public sealed class BookTrailerPackageTests
{
    private const string Book = "The storm reached the harbour before the first boat returned.\n\n"
        + "Mara found a sealed map beneath the old lighthouse floor.\n\n"
        + "At dawn the crew followed the marks across the quiet bay.\n\n"
        + "The island held a garden no one had visited for years.\n\n"
        + "They carried the seeds home before the next storm arrived.";

    [Fact]
    public async Task Reads_a_txt_book_with_source_hash_and_locators()
    {
        var bytes = Encoding.UTF8.GetBytes(Book);
        using var stream = new MemoryStream(bytes);
        var source = await BookTrailerSourceReader.ReadAsync(stream, "harbour.txt");
        Assert.Equal("text/plain", source.MediaType);
        Assert.Equal(bytes.Length, source.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), source.Sha256);
        Assert.Equal(5, source.Passages.Count);
        Assert.Equal("paragraph 1", source.Passages[0].Locator);
    }

    [Fact]
    public async Task Rejects_short_and_oversize_sources()
    {
        using var shortBook = new MemoryStream(Encoding.UTF8.GetBytes("One line."));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BookTrailerSourceReader.ReadAsync(shortBook, "short.txt"));
        using var oversized = new MemoryStream(new byte[BookTrailerSourceReader.MaxSourceBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BookTrailerSourceReader.ReadAsync(oversized, "huge.txt"));
    }

    [Fact]
    public async Task Reads_epub_in_spine_order()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "META-INF/container.xml", """
                <container><rootfiles><rootfile full-path="OEBPS/book.opf"/></rootfiles></container>
                """);
            Add(zip, "OEBPS/book.opf", """
                <package><manifest><item id="c1" href="chapter.xhtml"/></manifest>
                <spine><itemref idref="c1"/></spine></package>
                """);
            Add(zip, "OEBPS/chapter.xhtml", "<html><body>"
                + string.Join(string.Empty, Book.Split("\n\n").Select(p => $"<p>{p}</p>"))
                + "</body></html>");
        }
        buffer.Position = 0;
        var source = await BookTrailerSourceReader.ReadAsync(buffer, "harbour.epub");
        Assert.Equal(5, source.Passages.Count);
        Assert.StartsWith("OEBPS/chapter.xhtml#passage-1", source.Passages[0].Locator);
    }

    [Fact]
    public async Task Reads_a_pdf_with_page_locators()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var passage in Book.Split("\n\n"))
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(passage, 12, new PdfPoint(25, 700), font);
        }
        using var input = new MemoryStream(builder.Build());
        var source = await BookTrailerSourceReader.ReadAsync(input, "harbour.pdf");
        Assert.Equal(5, source.Passages.Count);
        Assert.StartsWith("page 1", source.Passages[0].Locator);
        Assert.StartsWith("page 5", source.Passages[4].Locator);
    }

    [Fact]
    public async Task Package_is_private_source_linked_and_hashes_each_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-trailer-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Book));
            var source = await BookTrailerSourceReader.ReadAsync(input, "harbour.txt");
            var path = await BookTrailerPackage.CreateAsync(source, "Harbour", root,
                new StubEncoder(), rightsConfirmed: true, score: new StubScore());
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(6, zip.Entries.Count);
            using var manifest = JsonDocument.Parse(Read(zip, "manifest.json"));
            var data = manifest.RootElement;
            Assert.Equal("book-trailer", data.GetProperty("kind").GetString());
            Assert.Equal("draft", data.GetProperty("status").GetString());
            Assert.False(data.GetProperty("published").GetBoolean());
            Assert.True(data.GetProperty("rightsConfirmed").GetBoolean());
            Assert.Equal(30, data.GetProperty("targetSeconds").GetInt32());
            Assert.Equal(source.Sha256, data.GetProperty("sourceSha256").GetString());
            foreach (var entry in data.GetProperty("files").EnumerateArray())
            {
                var bytes = Read(zip, entry.GetProperty("path").GetString()!);
                Assert.Equal(bytes.Length, entry.GetProperty("bytes").GetInt32());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.GetProperty("sha256").GetString());
            }
            using var script = JsonDocument.Parse(Read(zip, "script.json"));
            Assert.Equal(5, script.RootElement.GetProperty("shots").GetArrayLength());
            Assert.Equal("paragraph 1", script.RootElement.GetProperty("shots")[0]
                .GetProperty("sourceLocator").GetString());
            foreach (var shot in script.RootElement.GetProperty("shots").EnumerateArray())
                Assert.All(shot.GetProperty("onScreenText").GetString()!.Split('\n'),
                    line => Assert.True(line.Length <= 29, $"Card line is too wide: {line}"));
            Assert.Contains("00:00:30,000", Encoding.UTF8.GetString(Read(zip, "captions.srt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Refuses_to_export_without_rights()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Book));
        var source = await BookTrailerSourceReader.ReadAsync(input, "harbour.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BookTrailerPackage.CreateAsync(source, "Harbour", "unused", new StubEncoder(), false));
    }

    [Fact]
    public async Task Real_encoder_makes_a_playable_trailer_package()
    {
        var ffmpeg = FfmpegMediaExport.Find();
        Assert.True(File.Exists(ffmpeg), $"The bundled or installed FFmpeg is required: {ffmpeg}");
        var root = Path.Combine(Path.GetTempPath(), "concierge-trailer-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Book));
            var source = await BookTrailerSourceReader.ReadAsync(input, "harbour.txt");
            var path = await BookTrailerPackage.CreateAsync(source, "Harbour", root,
                new FfmpegMediaExport(ffmpeg), rightsConfirmed: true);
            using var zip = ZipFile.OpenRead(path);
            var video = Read(zip, "trailer.mp4");
            Assert.True(video.Length > 1_000);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(video, 4, 4));
            var videoPath = Path.Combine(root, "check.mp4");
            var pcmPath = Path.Combine(root, "check.pcm");
            await File.WriteAllBytesAsync(videoPath, video);
            var decoded = await new ProcessEncoder(ffmpeg).RunAsync(
                ["-hide_banner", "-loglevel", "error", "-y", "-i", videoPath,
                 "-vn", "-t", "2", "-ac", "1", "-ar", "8000", "-f", "s16le", pcmPath]);
            Assert.True(decoded.Ok, decoded.Said);
            var pcm = await File.ReadAllBytesAsync(pcmPath);
            Assert.True(pcm.Length > 8_000);
            var peak = Enumerable.Range(0, pcm.Length / 2)
                .Max(index => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2, 2))));
            Assert.True(peak > 1_000, $"The trailer soundtrack is effectively silent: peak={peak}");
            var sample = Environment.GetEnvironmentVariable("CONCIERGE_TRAILER_DRAFT_SAMPLE");
            if (!string.IsNullOrWhiteSpace(sample)) File.Copy(path, sample, overwrite: true);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Add(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(text);
    }

    private static byte[] Read(ZipArchive zip, string path)
    {
        using var input = zip.GetEntry(path)!.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private sealed class StubEncoder : IMediaExport
    {
        public Task<ExportResult> SoundAsync(DesignDocument document, string outputPath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<ExportResult> VideoAsync(DesignDocument document, string outputPath,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(DesignMedium.Motion, document.Medium);
            Assert.Equal(5, document.Frames.Count);
            Assert.All(document.Frames, shot => Assert.Equal(6, DesignTiming.Of(shot, 4).Seconds));
            var bytes = new byte[32];
            "ftyp"u8.CopyTo(bytes.AsSpan(4));
            await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
            return ExportResult.Made(outputPath);
        }
    }

    private sealed class StubScore : IBookTrailerScore
    {
        public Task AddAsync(string videoPath, string outputPath, string scratchDirectory,
            CancellationToken cancellationToken = default)
        {
            File.Copy(videoPath, outputPath);
            return Task.CompletedTask;
        }
    }
}
