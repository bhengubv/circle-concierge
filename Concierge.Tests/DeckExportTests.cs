using System.IO.Compression;
using System.Xml.Linq;
using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A deck as a .pptx somebody else can open and edit.
///
/// The reason this is worth the XML: a deck that leaves Concierge as a picture is
/// a deck nobody can change. Somebody who needs to fix one word has to come back
/// and ask. open-design exports PPTX for exactly that, and it is the difference
/// between a tool a team can use and a tool one person can use.
///
/// **These tests open the file back up.** A .pptx that is written and never read
/// is a zip nobody has checked — and PowerPoint is strict enough that "it wrote
/// without throwing" says almost nothing. So every part is parsed as XML, the
/// relationships are followed, and the words are found where a reader would look
/// for them.
/// </summary>
public sealed class DeckExportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "concierge-pptx-tests", Guid.NewGuid().ToString("N"));

    public DeckExportTests() => Directory.CreateDirectory(_root);

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

    private const string Dot =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private string Out(string name) => Path.Combine(_root, name);

    private static DesignDocument Deck(params string[] slideTitles)
    {
        var deck = DesignDocument.Blank(medium: DesignMedium.Deck);

        foreach (var title in slideTitles)
        {
            deck = deck.Add(DesignNode.New(DesignNodeKind.Frame, null, ("text", title)));
        }

        return deck;
    }

    private static ZipArchive Open(string path) => ZipFile.OpenRead(path);

    private static XDocument Part(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        Assert.NotNull(entry);

        using var stream = entry!.Open();
        return XDocument.Load(stream);
    }

    private static string AllText(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }

    // ── It is a real package ──────────────────────────────────────────────

    /// <summary>
    /// PowerPoint is strict, and "it wrote without throwing" says almost nothing.
    /// Every part has to be there and every one has to be XML.
    /// </summary>
    [Fact]
    public void Every_part_powerpoint_looks_for_is_there_and_parses()
    {
        var path = Out("deck.pptx");
        Assert.True(DeckExport.Write(Deck("One", "Two"), path).Ok);

        using var zip = Open(path);

        foreach (var part in new[]
                 {
                     "[Content_Types].xml",
                     "_rels/.rels",
                     "ppt/presentation.xml",
                     "ppt/_rels/presentation.xml.rels",
                     "ppt/theme/theme1.xml",
                     "ppt/slideMasters/slideMaster1.xml",
                     "ppt/slideMasters/_rels/slideMaster1.xml.rels",
                     "ppt/slideLayouts/slideLayout1.xml",
                     "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
                     "ppt/slides/slide1.xml",
                     "ppt/slides/_rels/slide1.xml.rels",
                     "ppt/slides/slide2.xml",
                 })
        {
            Assert.NotNull(Part(zip, part));
        }
    }

    /// <summary>
    /// Every part declared in the content types has to exist, and every slide has
    /// to be declared. A part that is present and undeclared is a file PowerPoint
    /// refuses to open at all.
    /// </summary>
    [Fact]
    public void What_is_declared_and_what_is_present_agree()
    {
        var path = Out("agree.pptx");
        Assert.True(DeckExport.Write(Deck("One", "Two", "Three"), path).Ok);

        using var zip = Open(path);
        var types = AllText(zip, "[Content_Types].xml");

        for (var at = 1; at <= 3; at++)
        {
            Assert.Contains($"/ppt/slides/slide{at}.xml", types, StringComparison.Ordinal);
            Assert.NotNull(zip.GetEntry($"ppt/slides/slide{at}.xml"));
        }
    }

    /// <summary>
    /// Every relationship has to point at something that is in the zip. A dangling
    /// one is the commonest way a hand-written package opens as "repair?".
    /// </summary>
    [Fact]
    public void No_relationship_points_at_something_that_is_not_there()
    {
        var path = Out("rels.pptx");

        var deck = Deck("With a picture");
        var slide = DesignMediums.FramesOf(deck)[0];
        deck = deck.Add(DesignNode.New(DesignNodeKind.Image, slide.Id, ("src", Dot)));

        Assert.True(DeckExport.Write(deck, path).Ok);

        using var zip = Open(path);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)).ToList())
        {
            var folder = entry.FullName[..entry.FullName.LastIndexOf("_rels/", StringComparison.Ordinal)];
            var rels = Part(zip, entry.FullName);

            foreach (var target in rels.Root!.Elements().Select(e => e.Attribute("Target")!.Value))
            {
                // Resolved the way a reader resolves it: relative to the part's own
                // folder, with ../ climbing out. Done by hand rather than with
                // Path.GetFullPath, which anchors to the drive and turned every
                // target into C:/something.
                var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));

                foreach (var step in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (step == "..")
                    {
                        if (parts.Count > 0)
                        {
                            parts.RemoveAt(parts.Count - 1);
                        }
                    }
                    else if (step != ".")
                    {
                        parts.Add(step);
                    }
                }

                var resolved = string.Join('/', parts);

                Assert.True(names.Contains(resolved), $"{entry.FullName} points at missing {resolved}");
            }
        }
    }

    // ── What is on the slides ─────────────────────────────────────────────

    [Fact]
    public void One_slide_per_frame()
    {
        var path = Out("count.pptx");
        Assert.True(DeckExport.Write(Deck("A", "B", "C", "D"), path).Ok);

        using var zip = Open(path);

        Assert.Equal(4, zip.Entries.Count(e =>
            e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
            && e.FullName.EndsWith(".xml", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_words_on_a_slide_are_in_the_file()
    {
        var path = Out("words.pptx");

        var deck = Deck("The quarter in review");
        var slide = DesignMediums.FramesOf(deck)[0];
        deck = deck.Add(DesignNode.New(DesignNodeKind.Text, slide.Id, ("text", "Revenue held steady")));

        Assert.True(DeckExport.Write(deck, path).Ok);

        using var zip = Open(path);
        var xml = AllText(zip, "ppt/slides/slide1.xml");

        Assert.Contains("The quarter in review", xml, StringComparison.Ordinal);
        Assert.Contains("Revenue held steady", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// A slide's words come from a person, and an ampersand is a person's
    /// punctuation and XML's syntax. Unescaped, it is a file that will not open.
    /// </summary>
    [Fact]
    public void Punctuation_that_would_break_the_xml_does_not()
    {
        var path = Out("awkward.pptx");
        Assert.True(DeckExport.Write(Deck("Sales & Marketing <2026>"), path).Ok);

        using var zip = Open(path);

        // It parses — which is the whole assertion — and the words come back out as
        // the words. Read from the parsed text rather than the markup: the markup
        // correctly holds "&amp;", and asserting on that would be checking the
        // escaping instead of the round trip.
        var said = Part(zip, "ppt/slides/slide1.xml")
            .Descendants()
            .Where(node => node.Name.LocalName == "t")
            .Select(node => node.Value)
            .ToList();

        Assert.Contains("Sales & Marketing <2026>", said);
    }

    /// <summary>
    /// A deck that arrived in PowerPoint's default blue would have thrown away the
    /// one thing the person actually chose.
    /// </summary>
    [Fact]
    public void The_design_keeps_its_own_colours()
    {
        var path = Out("night.pptx");
        Assert.True(DeckExport.Write(Deck("Dark").Wearing("Night"), path).Ok);

        var look = DesignLooks.Of("Night");

        using var zip = Open(path);

        Assert.Contains(
            look.Ground.TrimStart('#').ToUpperInvariant(),
            AllText(zip, "ppt/slides/slide1.xml"),
            StringComparison.Ordinal);

        Assert.Contains(
            look.Accent.TrimStart('#').ToUpperInvariant(),
            AllText(zip, "ppt/theme/theme1.xml"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_on_a_slide_travels_with_it()
    {
        var path = Out("picture.pptx");

        var deck = Deck("Look at this");
        var slide = DesignMediums.FramesOf(deck)[0];
        deck = deck.Add(DesignNode.New(DesignNodeKind.Image, slide.Id, ("src", Dot)));

        Assert.True(DeckExport.Write(deck, path).Ok);

        using var zip = Open(path);

        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal));
        Assert.Contains("r:embed", AllText(zip, "ppt/slides/slide1.xml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A picture that is not the base64 it claims to be costs the picture, not the
    /// deck. Losing a whole presentation over one bad paste would be the wrong
    /// trade.
    /// </summary>
    [Fact]
    public void A_broken_picture_costs_the_picture_and_not_the_deck()
    {
        var path = Out("bad-picture.pptx");

        var deck = Deck("Still fine");
        var slide = DesignMediums.FramesOf(deck)[0];
        deck = deck.Add(DesignNode.New(DesignNodeKind.Image, slide.Id, ("src", "data:image/png;base64,???")));

        Assert.True(DeckExport.Write(deck, path).Ok);

        using var zip = Open(path);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal));
        Assert.Contains("Still fine", AllText(zip, "ppt/slides/slide1.xml"), StringComparison.Ordinal);
    }

    // ── Edges ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A deck nobody has added a slide to is still a thing somebody wants to save.
    /// Refusing it means telling them their work cannot be kept because they have
    /// not pressed a button nobody told them about.
    /// </summary>
    [Fact]
    public void A_deck_with_no_slides_yet_still_saves()
    {
        var path = Out("empty.pptx");

        Assert.True(DeckExport.Write(DesignDocument.Blank(medium: DesignMedium.Deck), path).Ok);

        using var zip = Open(path);
        Assert.NotNull(zip.GetEntry("ppt/slides/slide1.xml"));
    }

    /// <summary>
    /// Slide ids below 256 are reserved, and PowerPoint refuses the file rather
    /// than renumbering them.
    /// </summary>
    [Fact]
    public void Slide_ids_start_where_powerpoint_allows()
    {
        var path = Out("ids.pptx");
        Assert.True(DeckExport.Write(Deck("A", "B"), path).Ok);

        using var zip = Open(path);
        var xml = AllText(zip, "ppt/presentation.xml");

        Assert.Contains("""id="256""", xml, StringComparison.Ordinal);
        Assert.Contains("""id="257""", xml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Written beside and moved into place, the rule the design store follows. A
    /// save interrupted halfway would otherwise leave a zip that is not a zip
    /// where somebody's deck used to be.
    /// </summary>
    [Fact]
    public void Nothing_is_left_behind_half_written()
    {
        var path = Out("clean.pptx");
        Assert.True(DeckExport.Write(Deck("A"), path).Ok);

        Assert.False(File.Exists(path + ".part"));
        Assert.True(File.Exists(path));
    }
}
