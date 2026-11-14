using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Concierge.Shared.BookTrailers;

namespace Concierge.Tests;

/// <summary>
/// Real book bytes in, ordered and traceable source passages out.
/// </summary>
public sealed class BookTrailerSourceAcceptanceTests
{
    [Fact]
    public async Task Txt_keeps_the_end_of_the_book_and_records_the_exact_source_hash()
    {
        var text = string.Join("\n", Enumerable.Range(1, 500)
            .Select(number => $"Chapter {number}: The lantern reveals clue {number}."));
        var bytes = Encoding.UTF8.GetBytes(text);

        var book = await BookSourceReader.ReadAsync(new MemoryStream(bytes), "lantern.txt");

        Assert.Equal("txt", book.Format);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), book.Sha256);
        Assert.Contains("clue 1", string.Join(" ", book.Sections.Select(part => part.Text)));
        Assert.Contains("clue 500", string.Join(" ", book.Sections.Select(part => part.Text)));
        Assert.All(book.Sections, part => Assert.False(string.IsNullOrWhiteSpace(part.Locator)));
    }

    [Fact]
    public async Task Epub_reads_chapters_in_spine_order_with_source_locations()
    {
        var book = await BookSourceReader.ReadAsync(new MemoryStream(Epub()), "lantern.epub");

        Assert.Equal("epub", book.Format);
        Assert.Equal("The Lantern", book.Title);
        Assert.Equal(2, book.Sections.Count);
        Assert.Contains("first clue", book.Sections[0].Text);
        Assert.Contains("final clue", book.Sections[1].Text);
        Assert.All(book.Sections, part => Assert.False(string.IsNullOrWhiteSpace(part.Locator)));
    }

    [Fact]
    public async Task Text_pdf_reads_visible_words_and_page_number()
    {
        var book = await BookSourceReader.ReadAsync(new MemoryStream(Pdf("The lantern reveals the first clue.")), "lantern.pdf");

        Assert.Equal("pdf", book.Format);
        Assert.Contains(book.Sections, part => part.Locator.Contains('1')
            && part.Text.Contains("first clue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scanned_pdf_does_not_pretend_it_has_read_the_book()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            BookSourceReader.ReadAsync(new MemoryStream(Pdf(null)), "scanned.pdf"));

        Assert.Contains("text", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_input_fails_with_a_useful_format_message()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            BookSourceReader.ReadAsync(new MemoryStream("hello"u8.ToArray()), "book.docx"));

        Assert.Contains("PDF", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EPUB", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TXT", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_stops_import_before_reading()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BookSourceReader.ReadAsync(new MemoryStream("hello"u8.ToArray()), "book.txt", cancelled.Token));
    }

    internal static byte[] Epub()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("OEBPS/content.opf", """
                <?xml version="1.0" encoding="utf-8"?>
                <package version="2.0" xmlns="http://www.idpf.org/2007/opf" unique-identifier="bookid"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>The Lantern</dc:title><dc:identifier id="bookid">urn:uuid:11111111-1111-1111-1111-111111111111</dc:identifier><dc:language>en</dc:language></metadata><manifest><item id="two" href="two.xhtml" media-type="application/xhtml+xml"/><item id="one" href="one.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="one"/><itemref idref="two"/></spine></package>
                """);
            Add("OEBPS/one.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><body><h1>One</h1><p>The first clue appears.</p></body></html>");
            Add("OEBPS/two.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><body><h1>Two</h1><p>The final clue appears.</p></body></html>");

            void Add(string name, string value, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(name, level).Open();
                var bytes = Encoding.UTF8.GetBytes(value);
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    // A valid, one-page PDF with a standard font. This exercises actual PDF parsing,
    // rather than accepting a .pdf extension on plain text.
    internal static byte[] Pdf(string? line)
    {
        var content = line is null ? string.Empty : $"BT /F1 12 Tf 72 720 Td ({line}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
        };
        var file = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(file.ToString()));
            file.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(file.ToString());
        file.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) file.Append($"{offset:D10} 00000 n \n");
        file.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(file.ToString());
    }
}
