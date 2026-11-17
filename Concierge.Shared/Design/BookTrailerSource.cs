using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Concierge.Shared.Design;

public sealed record BookPassage(string Locator, string Text);

public sealed record BookTrailerSource(string FileName, string MediaType, long Bytes,
    string Sha256, IReadOnlyList<BookPassage> Passages);

/// <summary>Reads a local book without copying its full text into the SleptON! package.</summary>
public static partial class BookTrailerSourceReader
{
    public const int MaxSourceBytes = 8 * 1024 * 1024;

    public static async Task<BookTrailerSource> ReadAsync(Stream input, string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        fileName = Path.GetFileName(fileName ?? string.Empty);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var mediaType = extension switch
        {
            ".txt" => "text/plain",
            ".epub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            _ => throw new InvalidDataException("Choose a TXT, EPUB or PDF book.")
        };
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > MaxSourceBytes)
                throw new InvalidDataException("This book is over the 8 MB local import limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length == 0) throw new InvalidDataException("The book is empty.");
        // PDF layout and EPUB XML parsing are CPU work. Keep the BlazorWebView's
        // interaction thread responsive while a book is being read on a phone.
        var passages = await Task.Run(() => extension switch
        {
            ".txt" => TextPassages(StrictUtf8(bytes), "paragraph"),
            ".epub" => EpubPassages(bytes),
            _ => PdfPassages(bytes)
        }, cancellationToken);
        if (passages.Count < 5)
            throw new InvalidDataException("The book needs at least five readable passages for a 30-second trailer.");
        return new BookTrailerSource(fileName, mediaType, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), passages);
    }

    private static string StrictUtf8(byte[] bytes)
        => new UTF8Encoding(false, true).GetString(bytes);

    private static List<BookPassage> TextPassages(string text, string prefix)
    {
        var paragraphs = BlankLines().Split(text).Select(Normalize)
            .Where(value => value.Length >= 20).ToArray();
        if (paragraphs.Length < 5)
            paragraphs = SentenceBreaks().Split(Normalize(text))
                .Select(Normalize).Where(value => value.Length >= 20).ToArray();
        return paragraphs.Take(20_000).Select((value, index) =>
            new BookPassage($"{prefix} {index + 1}", value)).ToList();
    }

    private static List<BookPassage> PdfPassages(byte[] bytes)
    {
        if (bytes.Length < 8 || !bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new InvalidDataException("The PDF header is invalid.");
        var result = new List<BookPassage>();
        using var document = PdfDocument.Open(bytes);
        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            result.AddRange(TextPassages(text, $"page {page.Number}, passage"));
            if (result.Count >= 20_000) break;
        }
        return result;
    }

    private static List<BookPassage> EpubPassages(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > 2_000)
            throw new InvalidDataException("The EPUB has too many files.");
        long remainingXmlBytes = 16L * 1024 * 1024;
        var container = ReadXml(archive.GetEntry("META-INF/container.xml")
            ?? throw new InvalidDataException("The EPUB has no container."), ref remainingXmlBytes);
        var opfPath = container.Descendants().FirstOrDefault(node => node.Name.LocalName == "rootfile")?
            .Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(opfPath))
            throw new InvalidDataException("The EPUB has no reading order.");
        var opf = ReadXml(archive.GetEntry(opfPath)
            ?? throw new InvalidDataException("The EPUB reading order is missing."), ref remainingXmlBytes);
        var items = opf.Descendants().Where(node => node.Name.LocalName == "item")
            .Select(node => (Id: node.Attribute("id")?.Value, Href: node.Attribute("href")?.Value))
            .Where(item => item.Id is not null && item.Href is not null)
            .ToDictionary(item => item.Id!, item => item.Href!, StringComparer.Ordinal);
        var result = new List<BookPassage>();
        foreach (var reference in opf.Descendants().Where(node => node.Name.LocalName == "itemref"))
        {
            var id = reference.Attribute("idref")?.Value;
            if (id is null || !items.TryGetValue(id, out var href)) continue;
            var path = ResolveEpubPath(opfPath, href);
            var entry = archive.GetEntry(path);
            if (entry is null) continue;
            var chapter = ReadXml(entry, ref remainingXmlBytes);
            var count = 0;
            foreach (var node in chapter.Descendants().Where(node =>
                         node.Name.LocalName is "p" or "h1" or "h2" or "h3" or "blockquote"))
            {
                var text = Normalize(string.Concat(node.DescendantNodes().OfType<XText>().Select(part => part.Value)));
                if (text.Length < 20) continue;
                result.Add(new BookPassage($"{path}#passage-{++count}", text));
                if (result.Count >= 20_000) return result;
            }
        }
        return result;
    }

    private static XDocument ReadXml(ZipArchiveEntry entry, ref long remainingBytes)
    {
        if (entry.Length <= 0 || entry.Length > 4 * 1024 * 1024 || entry.Length > remainingBytes)
            throw new InvalidDataException("An EPUB chapter exceeds the local reading limit.");
        remainingBytes -= entry.Length;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        });
        return XDocument.Load(reader);
    }

    private static string ResolveEpubPath(string opfPath, string href)
    {
        var parts = new List<string>();
        var combined = opfPath[..Math.Max(0, opfPath.LastIndexOf('/') + 1)]
            + Uri.UnescapeDataString(href.Split('#')[0]);
        foreach (var part in combined.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("The EPUB contains an invalid chapter path.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static string Normalize(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"\r?\n\s*\r?\n")]
    private static partial Regex BlankLines();
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreaks();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
