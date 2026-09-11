using System.Text;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A design as a PDF — the format people actually send each other.
///
/// Written by hand rather than by driving a browser engine, and the reason is the
/// interesting part: the desktop head has WebView2 and could produce an exact copy
/// of the canvas, on Windows, on the desktop, and nowhere else. A person on the
/// web head or a phone would be told the thing they just made cannot be sent to
/// anybody. This lays it out from the same tree the renderers read, works
/// everywhere, and needs no dependency.
///
/// **These tests read the bytes back.** A PDF that is written and never opened is
/// a file nobody has checked, and every reader that matters is strict about the
/// cross-reference table — a wrong byte offset there is the difference between a
/// document and a repair prompt.
/// </summary>
public sealed class PdfExportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-pdf-tests", Guid.NewGuid().ToString("N"));

    public PdfExportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    private string Out(string name) => Path.Combine(_root, name);

    private static DesignDocument Deck(params string[] titles)
    {
        var deck = DesignDocument.Blank(medium: DesignMedium.Deck);

        foreach (var title in titles)
        {
            deck = deck.Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", title)));
        }

        return deck;
    }

    private static string Read(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    // ── It is a real PDF ──────────────────────────────────────────────────

    [Fact]
    public async Task It_starts_and_ends_the_way_a_pdf_does()
    {
        var path = Out("basic.pdf");
        Assert.True((await PdfExport.WriteAsync(Deck("Hello"), path)).Ok);

        var text = Read(path);

        Assert.StartsWith("%PDF-1.7", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every reader that matters goes to the cross-reference table first and jumps
    /// to the byte offsets in it. One wrong number there is the difference between
    /// a document and a repair prompt, so this walks them: each offset must land
    /// exactly on that object's own header.
    /// </summary>
    [Fact]
    public async Task Every_offset_in_the_table_lands_on_the_object_it_names()
    {
        var path = Out("offsets.pdf");
        Assert.True((await PdfExport.WriteAsync(Deck("One", "Two", "Three"), path)).Ok);

        var text = Read(path);

        var startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(startxref > 0);

        var where = int.Parse(
            text[(startxref + "startxref".Length)..].Trim().Split('\n')[0].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.StartsWith("xref", text[where..], StringComparison.Ordinal);

        // The table: "0 N", then N lines of "0000000000 00000 n ".
        var lines = text[where..].Split('\n');
        var count = int.Parse(lines[1].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);

        // Line 2 is the free entry for object 0; real objects start at line 3.
        for (var number = 1; number < count; number++)
        {
            var offset = int.Parse(lines[number + 2][..10], System.Globalization.CultureInfo.InvariantCulture);

            Assert.StartsWith($"{number} 0 obj", text[offset..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_catalogue_and_the_page_tree_point_at_each_other()
    {
        var path = Out("tree.pdf");
        Assert.True((await PdfExport.WriteAsync(Deck("A", "B"), path)).Ok);

        var text = Read(path);

        Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Pages", text, StringComparison.Ordinal);
        Assert.Contains("/Count 2", text, StringComparison.Ordinal);

        // Every page names the real page-tree object, not the placeholder it was
        // built with before that object existed.
        Assert.DoesNotContain("PAGES 0 R", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_page_per_frame()
    {
        var path = Out("count.pdf");
        Assert.True((await PdfExport.WriteAsync(Deck("A", "B", "C", "D"), path)).Ok);

        var text = Read(path);

        Assert.Equal(4, text.Split("/Type /Page ").Length - 1);
        Assert.Contains("/Count 4", text, StringComparison.Ordinal);
    }

    // ── What is on the page ───────────────────────────────────────────────

    [Fact]
    public async Task The_words_are_in_the_file()
    {
        var path = Out("words.pdf");

        var deck = Deck("The quarter in review");
        var slide = DesignMediums.FramesOf(deck)[0];
        deck = deck.Add(DesignNode.New(DesignNodeKind.Text, slide.Id, ("text", "Revenue held steady")));

        Assert.True((await PdfExport.WriteAsync(deck, path)).Ok);

        var text = Read(path);

        Assert.Contains("The quarter in review", text, StringComparison.Ordinal);
        Assert.Contains("Revenue held steady", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Brackets and the backslash are the syntax of a PDF string and also
    /// punctuation people type. Unescaped, they end the string early and the file
    /// is broken from that byte on.
    /// </summary>
    [Fact]
    public async Task Punctuation_that_would_end_the_string_early_does_not()
    {
        var path = Out("awkward.pdf");

        Assert.True((await PdfExport.WriteAsync(Deck(@"Revenue (up 12%) \ Q3"), path)).Ok);

        var text = Read(path);

        Assert.Contains(@"Revenue \(up 12%\) \\ Q3", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A design wearing Night that printed on white would be a different design.
    /// </summary>
    [Fact]
    public async Task The_design_keeps_its_own_colours()
    {
        var white = Out("light.pdf");
        var dark = Out("dark.pdf");

        Assert.True((await PdfExport.WriteAsync(Deck("Same words").Wearing("Plain"), white)).Ok);
        Assert.True((await PdfExport.WriteAsync(Deck("Same words").Wearing("Night"), dark)).Ok);

        // The ground is painted as a filled rectangle before anything else, so the
        // two files differ in their very first drawing operation.
        Assert.NotEqual(
            Read(white)[..Read(white).IndexOf(" re f", StringComparison.Ordinal)],
            Read(dark)[..Read(dark).IndexOf(" re f", StringComparison.Ordinal)]);
    }

    /// <summary>
    /// Nothing is embedded, so nothing needs a font file. The fourteen standard
    /// fonts are the ones every reader is required to have.
    /// </summary>
    [Fact]
    public async Task Type_needs_no_font_file()
    {
        var path = Out("fonts.pdf");
        Assert.True((await PdfExport.WriteAsync(Deck("Hello"), path)).Ok);

        var text = Read(path);

        Assert.Contains("/BaseFont /Helvetica", text, StringComparison.Ordinal);
        Assert.Contains("/BaseFont /Helvetica-Bold", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/FontFile", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Long words have to break onto more lines than short ones. Guessing an
    /// average character width gives lines that overrun on capitals and stop short
    /// on narrow letters, which is visible on exactly the words people notice.
    /// </summary>
    [Fact]
    public async Task Words_wrap_by_how_wide_they_actually_are()
    {
        var narrow = Out("narrow.pdf");
        var wide = Out("wide.pdf");

        var many = string.Join(' ', Enumerable.Repeat("iiiii", 60));
        var few = string.Join(' ', Enumerable.Repeat("WWWWW", 60));

        Assert.True((await PdfExport.WriteAsync(Body(many), narrow)).Ok);
        Assert.True((await PdfExport.WriteAsync(Body(few), wide)).Ok);

        // Same number of words; the wide ones need more lines, so more Tm
        // operators are written.
        Assert.True(
            Lines(Read(wide)) > Lines(Read(narrow)),
            $"{Lines(Read(wide))} wide lines should exceed {Lines(Read(narrow))} narrow ones");

        static int Lines(string text) => text.Split(" Tm ").Length - 1;

        static DesignDocument Body(string words)
        {
            var deck = DesignDocument.Blank(medium: DesignMedium.Deck);
            var slide = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Title"));

            return deck.Add(slide).Add(DesignNode.New(DesignNodeKind.Text, slide.Id, ("text", words)));
        }
    }

    // ── Pictures ──────────────────────────────────────────────────────────

    /// <summary>
    /// A PDF has to be told the pixel size of an image it carries; it will not
    /// work it out. This is the marker walk that reads it.
    /// </summary>
    [Fact]
    public void A_jpegs_size_is_read_from_its_own_markers()
    {
        // The smallest thing shaped like a JPEG: start of image, then a start-of-
        // frame carrying 8 bits, 16 tall, 32 wide.
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x20,
        ];

        Assert.Equal((32, 16), PdfExport.SizeOfJpeg(jpeg));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[0])]
    public void Something_that_is_not_a_jpeg_has_no_size(byte[] bytes)
        => Assert.Null(PdfExport.SizeOfJpeg(bytes));

    /// <summary>
    /// A picture that cannot be carried correctly is left out rather than embedded
    /// as bytes a reader will render as noise — and the document still goes.
    /// </summary>
    [Fact]
    public async Task A_picture_that_cannot_be_converted_costs_the_picture_and_not_the_document()
    {
        var path = Out("no-picture.pdf");

        var deck = Deck("Still fine");
        var slide = DesignMediums.FramesOf(deck)[0];

        deck = deck.Add(DesignNode.New(
            DesignNodeKind.Image, slide.Id,
            ("src", "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")));

        // No encoder given, so a PNG cannot become a JPEG.
        Assert.True((await PdfExport.WriteAsync(deck, path, encoder: null)).Ok);

        var text = Read(path);

        Assert.DoesNotContain("/DCTDecode", text, StringComparison.Ordinal);
        Assert.Contains("Still fine", text, StringComparison.Ordinal);
    }

    // ── Edges ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_design_with_nothing_in_it_yet_still_saves()
    {
        var path = Out("empty.pdf");

        Assert.True((await PdfExport.WriteAsync(DesignDocument.Blank(medium: DesignMedium.Page), path)).Ok);
        Assert.Contains("/Count 1", Read(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_left_behind_half_written()
    {
        var path = Out("clean.pdf");

        Assert.True((await PdfExport.WriteAsync(Deck("A"), path)).Ok);

        Assert.False(File.Exists(path + ".part"));
        Assert.True(File.Exists(path));
    }
}
