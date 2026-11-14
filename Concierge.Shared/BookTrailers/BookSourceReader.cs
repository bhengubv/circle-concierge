using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Concierge.Shared.BookTrailers;

/// <summary>A source passage with a location a reviewer can find in the original book.</summary>
public sealed record BookSourceSection(string Locator, string Text);

/// <summary>Extracted book text. The hash identifies the exact imported source bytes.</summary>
public sealed record BookSourceDocument(
    string Title,
    string Format,
    string Sha256,
    IReadOnlyList<BookSourceSection> Sections);

/// <summary>
/// Reads book text locally. No URLs, subprocesses, cloud services, or writes are involved.
/// The resulting source locators let the later trailer writer check every claim.
/// </summary>
public static class BookSourceReader
{
    private const int MaxSourceBytes = 100 * 1024 * 1024;
    private const int MaxExtractedCharacters = 20_000_000;
    private const int MaxEpubEntries = 10_000;

    public static async Task<BookSourceDocument> ReadAsync(
        Stream source,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new InvalidDataException("The book file cannot be read.");

        var format = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".txt" => "txt",
            ".pdf" => "pdf",
            ".epub" => "epub",
            _ => throw new InvalidDataException("Choose a PDF, EPUB, or TXT book."),
        };

        var bytes = await ReadBoundedAsync(source, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) throw new InvalidDataException("The book file is empty.");
        // PDF/EPUB parsing and hashing are CPU work. Yield the Blazor/MAUI UI
        // context while they run, including when the upload stream is in memory.
        var (hash, title, sections) = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var sourceTitle = Path.GetFileNameWithoutExtension(fileName);
            IReadOnlyList<BookSourceSection> sourceSections;
            if (format == "epub") sourceSections = ReadEpub(bytes, cancellationToken, out sourceTitle);
            else if (format == "pdf") sourceSections = ReadPdf(bytes, cancellationToken);
            else sourceSections = ReadText(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            return (sourceHash, sourceTitle, sourceSections);
        }, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(fileName);

        if (sections.Count == 0 || sections.All(part => string.IsNullOrWhiteSpace(part.Text)))
            throw new InvalidDataException("No readable text was found in this book. A scanned PDF needs OCR first.");
        if (sections.Sum(part => (long)part.Text.Length) > MaxExtractedCharacters)
            throw new InvalidDataException("The extracted book text is too large for this device.");
        return new(title, format, hash, sections);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        if (source.CanSeek && source.Length - source.Position > MaxSourceBytes)
            throw new InvalidDataException("The book file is too large. The limit is 100 MB.");
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaxSourceBytes)
                throw new InvalidDataException("The book file is too large. The limit is 100 MB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static IReadOnlyList<BookSourceSection> ReadText(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            if (text.Length > MaxExtractedCharacters)
                throw new InvalidDataException("The extracted book text is too large for this device.");
            return [new("TXT: complete file", text)];
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("This TXT file is not valid UTF-8 text.", error);
        }
    }

    private static IReadOnlyList<BookSourceSection> ReadPdf(byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            using var document = PdfDocument.Open(bytes);
            var sections = new List<BookSourceSection>();
            long characters = 0;
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(document.GetPage(pageNumber)).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                characters += text.Length;
                if (characters > MaxExtractedCharacters)
                    throw new InvalidDataException("The extracted book text is too large for this device.");
                sections.Add(new($"PDF: page {pageNumber}", text));
            }
            return sections;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception error)
        {
            throw new InvalidDataException("The PDF could not be read. Check whether it is damaged or password protected.", error);
        }
    }

    private static IReadOnlyList<BookSourceSection> ReadEpub(byte[] bytes, CancellationToken cancellationToken,
        out string title)
    {
        title = string.Empty;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEpubEntries)
                throw new InvalidDataException("This EPUB has too many files to read safely.");
            var container = ReadXml(zip.GetEntry("META-INF/container.xml")
                ?? throw new InvalidDataException("This EPUB is missing its container file."));
            var opfPath = container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")
                ?.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(opfPath))
                throw new InvalidDataException("This EPUB has no package document.");
            var opf = ReadXml(zip.GetEntry(opfPath)
                ?? throw new InvalidDataException("This EPUB package document is missing."));
            title = opf.Descendants().FirstOrDefault(element => element.Name.LocalName == "title")?.Value.Trim()
                ?? string.Empty;
            var manifest = opf.Descendants().Where(element => element.Name.LocalName == "item")
                .Select(element => new
                {
                    Id = element.Attribute("id")?.Value,
                    Href = element.Attribute("href")?.Value,
                    MediaType = element.Attribute("media-type")?.Value,
                })
                .Where(item => item.Id is not null && item.Href is not null)
                .ToDictionary(item => item.Id!, StringComparer.Ordinal);
            var sections = new List<BookSourceSection>();
            long characters = 0;
            foreach (var itemRef in opf.Descendants().Where(element => element.Name.LocalName == "itemref"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = itemRef.Attribute("idref")?.Value;
                if (id is null || !manifest.TryGetValue(id, out var item)
                    || item.MediaType != "application/xhtml+xml") continue;
                var path = ResolveEpubPath(opfPath, item.Href!);
                var chapter = ReadXml(zip.GetEntry(path)
                    ?? throw new InvalidDataException($"This EPUB is missing a chapter: {path}."));
                var body = chapter.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
                if (body is null) continue;
                var text = string.Join(" ", body.DescendantNodes().OfType<XText>()
                    .Where(node => !node.Ancestors().Any(parent => parent.Name.LocalName is "script" or "style"))
                    .Select(node => node.Value.Trim()).Where(value => value.Length > 0));
                if (text.Length == 0) continue;
                characters += text.Length;
                if (characters > MaxExtractedCharacters)
                    throw new InvalidDataException("The extracted book text is too large for this device.");
                sections.Add(new($"EPUB: chapter {sections.Count + 1} ({path})", text));
            }
            return sections;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception error)
        {
            throw new InvalidDataException("The EPUB could not be read. Check whether it is damaged or protected.", error);
        }
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxExtractedCharacters)
            throw new InvalidDataException("An EPUB chapter is too large to read safely.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxExtractedCharacters,
        });
        return XDocument.Load(reader);
    }

    private static string ResolveEpubPath(string opfPath, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out _))
            throw new InvalidDataException("An EPUB chapter points outside the book.");
        var combined = string.Join('/', opfPath.Split('/').SkipLast(1).Append(href));
        var parts = new List<string>();
        foreach (var part in combined.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("An EPUB chapter points outside the book.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
