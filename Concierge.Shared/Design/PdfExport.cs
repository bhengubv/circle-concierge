using System.Globalization;
using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// A design as a PDF — the format people actually send each other.
///
/// **Written by hand, and the reason is worth stating rather than assumed.** The
/// obvious way to make a PDF from HTML is to drive a browser engine headlessly.
/// The desktop head has one in WebView2, and that route would give an exact copy
/// of the canvas — on Windows, on the desktop, and nowhere else. A person on the
/// web head or a phone would be told the thing they just made cannot be sent to
/// anybody.
///
/// So this lays the document out itself, from the same tree the renderers read. It
/// works on every head, needs no dependency at all, and produces a file that opens
/// in anything.
///
/// **What it costs, said plainly.** It is not a picture of the canvas. Text, the
/// look's colours, pictures and the order of things carry across; exact spacing,
/// web fonts and anything CSS does that this does not know about do not. A page of
/// words comes out looking like a page of words. A design leaning on a CSS trick
/// will not.
///
/// **Two decisions that make it dependency-free.** Type is one of the fourteen
/// fonts every PDF reader is required to have, so nothing is embedded and no font
/// file is needed; the wrapping uses Helvetica's real character widths, because
/// guessing an average width gives visibly ragged lines on exactly the words
/// people notice. And pictures ride as JPEG, which a PDF carries as-is — a PNG is
/// converted by the encoder when there is one, and left out when there is not
/// rather than embedded wrong.
/// </summary>
public static class PdfExport
{
    /// <summary>A4 at 72 points to the inch, landscape for a deck.</summary>
    private const double PageWide = 841.89;

    private const double PageTall = 595.28;

    private const double Margin = 56;

    /// <summary>Writes the design. Returns where it went, or why it did not.</summary>
    /// <param name="document">The design.</param>
    /// <param name="outputPath">Where to put it.</param>
    /// <param name="encoder">
    /// An encoder for turning a PNG into a JPEG a PDF can carry, or null. Without
    /// one the words and colours still come out; the pictures do not.
    /// </param>
    public static async Task<ExportResult> WriteAsync(
        DesignDocument document,
        string outputPath,
        FfmpegMediaExport? encoder = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var frames = DesignMediums.FramesOf(document);

        var pages = frames.Count > 0
            ? frames
            : [DesignNode.New(DesignNodeKind.Frame, null, ("text", string.Empty))];

        var look = DesignLooks.Of(document.Look);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var pdf = new Pdf();

            foreach (var page in pages)
            {
                var picture = await PictureAsync(document, page, encoder, cancellationToken).ConfigureAwait(false);
                pdf.AddPage(Draw(document, page, look, picture is not null), picture);
            }

            // Written beside and moved into place: a save interrupted halfway
            // otherwise leaves something that is not a PDF where a PDF was.
            var beside = outputPath + ".part";
            await File.WriteAllBytesAsync(beside, pdf.Finish(), cancellationToken).ConfigureAwait(false);
            File.Move(beside, outputPath, overwrite: true);

            return ExportResult.Made(outputPath);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return ExportResult.Failed(problem.Message);
        }
    }

    // ── Laying out one page ───────────────────────────────────────────────

    private static string Draw(DesignDocument document, DesignNode page, DesignLook look, bool hasPicture)
    {
        var inside = document.ChildrenOf(page.Id);
        var content = new StringBuilder();

        // The ground first, as a filled rectangle the size of the page. A design
        // wearing Night that printed on white would be a different design.
        var (gr, gg, gb) = Rgb(look.Ground);
        content.Append(CultureInfo.InvariantCulture,
            $"{gr} {gg} {gb} rg 0 0 {F(PageWide)} {F(PageTall)} re f\n");

        var (ir, ig, ib) = Rgb(look.Ink);
        var wide = hasPicture ? (PageWide / 2) - Margin - 12 : PageWide - (Margin * 2);
        var y = PageTall - Margin;

        var title = page.Text.Length > 0
            ? page.Text
            : inside.FirstOrDefault(node => node.Kind == DesignNodeKind.Heading)?.Text ?? string.Empty;

        if (title.Length > 0)
        {
            y = Write(content, title, Margin, y, wide, 26, bold: true, ir, ig, ib);
            y -= 14;
        }

        foreach (var node in inside)
        {
            if (node.Text.Length == 0 || string.Equals(node.Text, title, StringComparison.Ordinal))
            {
                continue;
            }

            var (size, bold) = node.Kind switch
            {
                DesignNodeKind.Heading => (18.0, true),
                DesignNodeKind.Button => (12.0, true),
                _ => (12.0, false),
            };

            if (y < Margin + size)
            {
                // Off the bottom of the page. Stopping is honest; running the
                // words off the edge would put them in the file and nowhere a
                // reader can see them.
                break;
            }

            y = Write(content, node.Text, Margin, y, wide, size, bold, ir, ig, ib);
            y -= size * 0.6;
        }

        if (hasPicture)
        {
            var left = PageWide / 2;
            var across = (PageWide / 2) - Margin;
            var down = PageTall - (Margin * 2);

            content.Append(CultureInfo.InvariantCulture,
                $"q {F(across)} 0 0 {F(down)} {F(left)} {F(Margin)} cm /Im1 Do Q\n");
        }

        return content.ToString();
    }

    /// <summary>
    /// One run of words, wrapped, top-down. Returns where the next line starts.
    /// </summary>
    private static double Write(
        StringBuilder content, string text, double x, double top, double wide,
        double size, bool bold, string r, string g, string b)
    {
        var font = bold ? "/F2" : "/F1";
        var y = top - size;

        content.Append(CultureInfo.InvariantCulture, $"{r} {g} {b} rg BT {font} {F(size)} Tf\n");

        foreach (var line in Wrap(text, wide, size, bold))
        {
            content.Append(CultureInfo.InvariantCulture,
                $"1 0 0 1 {F(x)} {F(y)} Tm ({Escape(line)}) Tj\n");

            y -= size * 1.3;
        }

        content.Append("ET\n");
        return y + (size * 1.3) - (size * 0.3);
    }

    /// <summary>
    /// Breaks words to a width, using Helvetica's real character widths.
    ///
    /// Guessing an average width was tried in the head and discarded before it was
    /// written: it gives lines that overrun on capitals and stop short on "iii",
    /// and the words people notice are the ones that look wrong.
    /// </summary>
    private static List<string> Wrap(string text, double wide, double size, bool bold)
    {
        var lines = new List<string>();

        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();

            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";

                if (Width(candidate, size, bold) <= wide || line.Length == 0)
                {
                    line.Clear();
                    line.Append(candidate);
                    continue;
                }

                lines.Add(line.ToString());
                line.Clear();
                line.Append(word);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    private static double Width(string text, double size, bool bold)
        => text.Sum(c => WidthOf(c, bold)) / 1000.0 * size;

    /// <summary>
    /// Helvetica's widths, in thousandths of an em, for the characters people
    /// type. Anything outside this range is given the width of a lowercase n,
    /// which is close enough that a stray symbol does not throw a line out.
    /// </summary>
    private static int WidthOf(char c, bool bold)
    {
        var at = c - 32;

        if (at < 0 || at >= Regular.Length)
        {
            return bold ? 611 : 556;
        }

        return bold ? Bold[at] : Regular[at];
    }

    private static readonly int[] Regular =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly int[] Bold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    // ── Pictures ──────────────────────────────────────────────────────────

    /// <summary>
    /// The first picture on a page as a JPEG, or null.
    ///
    /// A PDF carries a JPEG exactly as it is, which is why that is the format used.
    /// A PNG is converted by the encoder when there is one and **left out when
    /// there is not** — a picture that cannot be carried correctly is better
    /// missing than embedded as bytes a reader will render as noise.
    /// </summary>
    private static async Task<Picture?> PictureAsync(
        DesignDocument document, DesignNode page, FfmpegMediaExport? encoder, CancellationToken cancellationToken)
    {
        foreach (var node in document.ChildrenOf(page.Id))
        {
            if (node.Kind != DesignNodeKind.Image
                || !node.Props.TryGetValue("src", out var src)
                || !src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var comma = src.IndexOf(',');

            if (comma < 0)
            {
                continue;
            }

            byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(src[(comma + 1)..]);
            }
            catch (FormatException)
            {
                continue;
            }

            var already = src[..comma].Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                || src[..comma].Contains("jpg", StringComparison.OrdinalIgnoreCase);

            if (!already)
            {
                if (encoder is null)
                {
                    continue;
                }

                var converted = await encoder.ToJpegAsync(bytes, cancellationToken).ConfigureAwait(false);

                if (converted is null)
                {
                    continue;
                }

                bytes = converted;
            }

            if (SizeOfJpeg(bytes) is not { } size)
            {
                continue;
            }

            return new Picture(bytes, size.Wide, size.Tall);
        }

        return null;
    }

    /// <summary>
    /// A JPEG's size, read from its start-of-frame marker.
    ///
    /// A PDF has to be told the pixel dimensions of an image it carries; it will
    /// not work them out. Walking the markers is a dozen lines and avoids decoding
    /// anything.
    /// </summary>
    public static (int Wide, int Tall)? SizeOfJpeg(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return null;
        }

        var at = 2;

        // at + 8 is the last byte read below, so the guard is that it exists. It
        // was at + 9 < length, which is one too strict: a start-of-frame that is
        // the last thing in the file was skipped and the picture came back with no
        // size, which reads as "not a JPEG".
        while (at + 8 < bytes.Length)
        {
            if (bytes[at] != 0xFF)
            {
                at++;
                continue;
            }

            var marker = bytes[at + 1];

            // Every start-of-frame carries the dimensions; which one it is says how
            // the image was encoded, which does not matter here.
            if (marker is >= 0xC0 and <= 0xCF
                && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                var tall = (bytes[at + 5] << 8) | bytes[at + 6];
                var wide = (bytes[at + 7] << 8) | bytes[at + 8];

                return wide > 0 && tall > 0 ? (wide, tall) : null;
            }

            at += 2 + ((bytes[at + 2] << 8) | bytes[at + 3]);
        }

        return null;
    }

    private sealed record Picture(byte[] Bytes, int Wide, int Tall);

    // ── The file itself ───────────────────────────────────────────────────

    /// <summary>
    /// The object graph, written out with a cross-reference table.
    ///
    /// A PDF is a list of numbered objects, a table saying where each one starts,
    /// and a trailer pointing at the table. Readers use those byte offsets, so they
    /// are counted as the bytes are written rather than worked out afterwards.
    /// </summary>
    private sealed class Pdf
    {
        private readonly List<byte[]> _objects = [];
        private readonly List<int> _pageObjects = [];

        public void AddPage(string content, object? picture)
        {
            var image = picture as Picture;

            var contentNumber = Add(Stream(content));
            var imageNumber = image is null ? 0 : Add(ImageObject(image));

            var resources = new StringBuilder("<< /Font << /F1 1 0 R /F2 2 0 R >>");

            if (imageNumber > 0)
            {
                resources.Append(CultureInfo.InvariantCulture, $" /XObject << /Im1 {imageNumber} 0 R >>");
            }

            resources.Append(" >>");

            _pageObjects.Add(Add(Text(
                $"<< /Type /Page /Parent PAGES 0 R /MediaBox [0 0 {F(PageWide)} {F(PageTall)}] "
                + $"/Resources {resources} /Contents {contentNumber} 0 R >>")));
        }

        public byte[] Finish()
        {
            // The two fonts are objects 1 and 2, reserved before anything else so a
            // page can name them while it is being built.
            _objects.Insert(0, Text("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
            _objects.Insert(1, Text("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));

            // Everything written after the fonts moved down by two.
            var pages = _pageObjects.Select(number => number + 2).ToList();

            var pagesNumber = _objects.Count + 1;
            var kids = string.Join(' ', pages.Select(number => $"{number} 0 R"));

            _objects.Add(Text($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>"));

            var catalogue = _objects.Count + 1;
            _objects.Add(Text($"<< /Type /Catalog /Pages {pagesNumber} 0 R >>"));

            var file = new MemoryStream();
            Write(file, "%PDF-1.7\n%âãÏÓ\n");

            var offsets = new List<int>();

            for (var at = 0; at < _objects.Count; at++)
            {
                offsets.Add((int)file.Length);

                var body = _objects[at];

                // The page objects were written before the page tree existed, so
                // its number is stamped in here rather than guessed at earlier.
                var text = Encoding.Latin1.GetString(body)
                    .Replace("PAGES 0 R", $"{pagesNumber} 0 R", StringComparison.Ordinal);

                Write(file, $"{at + 1} 0 obj\n");
                file.Write(Encoding.Latin1.GetBytes(text));
                Write(file, "\nendobj\n");
            }

            var xref = (int)file.Length;

            Write(file, $"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");

            foreach (var offset in offsets)
            {
                Write(file, offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            }

            Write(file,
                $"trailer\n<< /Size {_objects.Count + 1} /Root {catalogue} 0 R >>\nstartxref\n{xref}\n%%EOF\n");

            return file.ToArray();
        }

        private int Add(byte[] body)
        {
            _objects.Add(body);
            return _objects.Count;
        }

        private static byte[] Text(string body) => Encoding.Latin1.GetBytes(body);

        private static byte[] Stream(string content)
        {
            var bytes = Encoding.Latin1.GetBytes(content);

            var head = Encoding.Latin1.GetBytes($"<< /Length {bytes.Length} >>\nstream\n");
            var tail = Encoding.Latin1.GetBytes("\nendstream");

            return [.. head, .. bytes, .. tail];
        }

        private static byte[] ImageObject(Picture picture)
        {
            var head = Encoding.Latin1.GetBytes(
                $"<< /Type /XObject /Subtype /Image /Width {picture.Wide} /Height {picture.Tall} "
                + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {picture.Bytes.Length} >>\nstream\n");

            var tail = Encoding.Latin1.GetBytes("\nendstream");

            return [.. head, .. picture.Bytes, .. tail];
        }

        private static void Write(Stream into, string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            into.Write(bytes);
        }
    }

    // ── Small things ──────────────────────────────────────────────────────

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A colour as the three numbers a PDF wants, each nought to one.</summary>
    private static (string R, string G, string B) Rgb(string colour)
    {
        var hex = colour.TrimStart('#').Trim();

        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => $"{c}{c}"));
        }

        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
        {
            return ("0", "0", "0");
        }

        static string Part(string hex, int at)
            => (Convert.ToInt32(hex.Substring(at, 2), 16) / 255.0).ToString("0.###", CultureInfo.InvariantCulture);

        return (Part(hex, 0), Part(hex, 2), Part(hex, 4));
    }

    /// <summary>
    /// Words as a PDF string.
    ///
    /// Brackets and the backslash are the syntax of a PDF string, and they are also
    /// punctuation people type.
    ///
    /// **Typographic characters become their plain equivalents rather than being
    /// dropped**, which was the first version and was visibly wrong: "Concierge —
    /// Q3" came out as "Concierge  Q3", a double space that reads as a typo rather
    /// than as a missing character. Seen in the very first PDF this produced, and
    /// this repository's own prose is made of em dashes. The reader loses the
    /// typography and keeps the sentence, which is the right way round.
    ///
    /// Anything still outside Latin-1 after that is dropped rather than written as
    /// a byte meaning a different character: a mangled letter in somebody's name is
    /// worse than a missing one.
    /// </summary>
    private static string Escape(string text)
    {
        var built = new StringBuilder();

        foreach (var raw in text)
        {
            // An ellipsis is three characters, so it is handled before the rest.
            if (raw == '…')
            {
                built.Append("...");
                continue;
            }

            var c = raw switch
            {
                '—' or '–' => '-',
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                ' ' or ' ' or ' ' => ' ',
                _ => raw,
            };

            if (c is '(' or ')' or '\\')
            {
                built.Append('\\');
                built.Append(c);
            }
            else if (c >= ' ' && c <= 'ÿ')
            {
                built.Append(c);
            }
        }

        return built.ToString();
    }
}
