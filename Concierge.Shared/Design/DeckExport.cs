using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Concierge.Shared.Design;

/// <summary>
/// A deck as a .pptx somebody else can open and edit.
///
/// The reason this exists rather than "they can print it": a deck that leaves
/// Concierge as a picture is a deck nobody can change. Somebody in marketing who
/// needs to fix one word has to come back and ask. open-design exports PPTX for
/// exactly this reason, and it is the difference between a tool a team can use and
/// a tool one person can use.
///
/// **Written by hand, and no dependency was added for it.** A .pptx is a zip of
/// XML — the parts below are the smallest set PowerPoint will open: a content-type
/// map, a presentation, one master, one layout, a theme, and a slide each. Nothing
/// here needs a library; it needs the format written out correctly, which is work
/// rather than difficulty.
///
/// **What it produces, said plainly so nobody expects otherwise.** Each frame
/// becomes one slide with a title, its text beneath, and its picture if it has
/// one, on the design's own background. It is not a rendering of the canvas: the
/// canvas is HTML and this is PowerPoint's own layout engine, so the words and the
/// pictures and the colours carry across and the exact spacing does not. That is
/// the trade that makes the file *editable* — a pixel-perfect copy would have to
/// be an image, which is the thing this exists to avoid.
/// </summary>
public static class DeckExport
{
    /// <summary>A slide is this many EMUs across: 13.333in at 914,400 per inch.</summary>
    private const long SlideWide = 12192000;

    /// <summary>And this tall: 7.5in, so 16:9.</summary>
    private const long SlideTall = 6858000;

    private const long Margin = 685800;

    /// <summary>Writes the deck. Returns where it went, or why it did not.</summary>
    public static ExportResult Write(DesignDocument document, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var slides = DesignMediums.FramesOf(document);

        // A page is one slide. A deck with no frames yet is still a page of
        // something, so it is not refused — the alternative is telling somebody
        // their work cannot be saved because they have not pressed a button they
        // were never told about.
        var frames = slides.Count > 0
            ? slides
            : [DesignNode.New(DesignNodeKind.Frame, null, ("text", string.Empty))];

        var look = DesignLooks.Of(document.Look);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Written beside and moved into place, the same rule the design store
            // follows: a save interrupted halfway would otherwise leave a zip that
            // is not a zip where somebody's deck used to be.
            var beside = outputPath + ".part";

            using (var file = File.Create(beside))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var pictures = new Dictionary<int, (string Name, byte[] Bytes)>();

                for (var at = 0; at < frames.Count; at++)
                {
                    if (PictureIn(document, frames[at]) is { } picture)
                    {
                        pictures[at] = ($"image{at + 1}{picture.Extension}", picture.Bytes);
                    }
                }

                Put(zip, "[Content_Types].xml", ContentTypes(frames.Count, pictures));
                Put(zip, "_rels/.rels", RootRels);
                Put(zip, "ppt/presentation.xml", Presentation(frames.Count));
                Put(zip, "ppt/_rels/presentation.xml.rels", PresentationRels(frames.Count));
                Put(zip, "ppt/theme/theme1.xml", Theme(look));
                Put(zip, "ppt/slideMasters/slideMaster1.xml", SlideMaster);
                Put(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", SlideMasterRels);
                Put(zip, "ppt/slideLayouts/slideLayout1.xml", SlideLayout);
                Put(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", SlideLayoutRels);

                for (var at = 0; at < frames.Count; at++)
                {
                    var has = pictures.TryGetValue(at, out var picture);

                    Put(zip, $"ppt/slides/slide{at + 1}.xml", Slide(document, frames[at], look, has));
                    Put(zip, $"ppt/slides/_rels/slide{at + 1}.xml.rels", SlideRels(has ? picture.Name : null));

                    if (has)
                    {
                        var entry = zip.CreateEntry($"ppt/media/{picture.Name}", CompressionLevel.Fastest);
                        using var into = entry.Open();
                        into.Write(picture.Bytes);
                    }
                }
            }

            File.Move(beside, outputPath, overwrite: true);

            return ExportResult.Made(outputPath);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            return ExportResult.Failed(problem.Message);
        }
    }

    // ── The parts ─────────────────────────────────────────────────────────

    private static void Put(ZipArchive zip, string path, string xml)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var into = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        into.Write(xml);
    }

    private static string ContentTypes(int slides, Dictionary<int, (string Name, byte[] Bytes)> pictures)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");
        xml.Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""");
        xml.Append("""<Default Extension="xml" ContentType="application/xml"/>""");

        foreach (var extension in pictures.Values
                     .Select(picture => Path.GetExtension(picture.Name).TrimStart('.').ToLowerInvariant())
                     .Distinct())
        {
            var type = extension switch
            {
                "png" => "image/png",
                "jpg" or "jpeg" => "image/jpeg",
                "gif" => "image/gif",
                _ => "application/octet-stream",
            };

            xml.Append(CultureInfo.InvariantCulture, $"""<Default Extension="{extension}" ContentType="{type}"/>""");
        }

        xml.Append("""<Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>""");
        xml.Append("""<Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>""");
        xml.Append("""<Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>""");
        xml.Append("""<Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>""");

        for (var at = 1; at <= slides; at++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/ppt/slides/slide{at}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>""");
        }

        xml.Append("</Types>");
        return xml.ToString();
    }

    private const string RootRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/></Relationships>
        """;

    private static string Presentation(int slides)
    {
        var ids = new StringBuilder();

        for (var at = 1; at <= slides; at++)
        {
            // Slide ids start at 256 — below that is reserved, and PowerPoint
            // refuses the file rather than renumbering.
            ids.Append(CultureInfo.InvariantCulture, $"""<p:sldId id="{255 + at}" r:id="rId{at + 1}"/>""");
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx="{SlideWide}" cy="{SlideTall}"/><p:notesSz cx="{SlideTall}" cy="{SlideWide}"/></p:presentation>
            """;
    }

    private static string PresentationRels(int slides)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        xml.Append("""<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/>""");

        for (var at = 1; at <= slides; at++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{at + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide{at}.xml"/>""");
        }

        xml.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{slides + 2}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="theme/theme1.xml"/>""");
        xml.Append("</Relationships>");
        return xml.ToString();
    }

    private const string SlideMaster = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst></p:sldMaster>
        """;

    private const string SlideMasterRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="../theme/theme1.xml"/></Relationships>
        """;

    private const string SlideLayout = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldLayout xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" type="blank" preserve="1"><p:cSld name="Blank"><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMapOvr><a:overrideClrMapping bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/></p:clrMapOvr></p:sldLayout>
        """;

    private const string SlideLayoutRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>
        """;

    private static string SlideRels(string? pictureName)
    {
        var picture = pictureName is null
            ? string.Empty
            : $"""<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/{pictureName}"/>""";

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>{picture}</Relationships>
            """;
    }

    /// <summary>
    /// The design's own colours, as the theme every slide reads from.
    ///
    /// A deck that arrived in PowerPoint's default blue would have thrown away the
    /// one thing the person chose.
    /// </summary>
    private static string Theme(DesignLook look)
    {
        var ink = Hex(look.Ink);
        var ground = Hex(look.Ground);
        var accent = Hex(look.Accent);
        var raised = Hex(look.Raised);

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?><a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Concierge"><a:themeElements><a:clrScheme name="Concierge"><a:dk1><a:srgbClr val="{ink}"/></a:dk1><a:lt1><a:srgbClr val="{ground}"/></a:lt1><a:dk2><a:srgbClr val="{ink}"/></a:dk2><a:lt2><a:srgbClr val="{raised}"/></a:lt2><a:accent1><a:srgbClr val="{accent}"/></a:accent1><a:accent2><a:srgbClr val="{accent}"/></a:accent2><a:accent3><a:srgbClr val="{accent}"/></a:accent3><a:accent4><a:srgbClr val="{accent}"/></a:accent4><a:accent5><a:srgbClr val="{accent}"/></a:accent5><a:accent6><a:srgbClr val="{accent}"/></a:accent6><a:hlink><a:srgbClr val="{accent}"/></a:hlink><a:folHlink><a:srgbClr val="{accent}"/></a:folHlink></a:clrScheme><a:fontScheme name="Concierge"><a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Concierge"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>
            """;
    }

    /// <summary>One slide: a title, the words under it, and a picture if there is one.</summary>
    private static string Slide(DesignDocument document, DesignNode frame, DesignLook look, bool hasPicture)
    {
        var inside = document.ChildrenOf(frame.Id);

        var title = frame.Text.Length > 0
            ? frame.Text
            : inside.FirstOrDefault(node => node.Kind == DesignNodeKind.Heading)?.Text ?? string.Empty;

        var words = inside
            .Where(node => node.Kind is DesignNodeKind.Text or DesignNodeKind.Button)
            .Select(node => node.Text)
            .Where(text => text.Length > 0)
            .ToList();

        // Anything that is not the title and not body text still gets a line, so a
        // slide never silently loses something that is on the canvas.
        foreach (var other in inside.Where(node => node.Kind == DesignNodeKind.Heading).Skip(title.Length > 0 ? 0 : 1))
        {
            if (!string.Equals(other.Text, title, StringComparison.Ordinal) && other.Text.Length > 0)
            {
                words.Insert(0, other.Text);
            }
        }

        var shapes = new StringBuilder();
        var id = 2;

        shapes.Append(TextShape(
            id++, "Title", Margin, Margin, SlideWide - (Margin * 2), 1200000,
            [title.Length > 0 ? title : "Untitled"], 4000, true, look.Ink));

        if (words.Count > 0)
        {
            shapes.Append(TextShape(
                id++, "Body", Margin, Margin + 1400000,
                hasPicture ? (SlideWide / 2) - Margin : SlideWide - (Margin * 2),
                SlideTall - Margin - 1400000 - Margin,
                words, 2000, false, look.Ink));
        }

        if (hasPicture)
        {
            var left = SlideWide / 2;
            var wide = (SlideWide / 2) - Margin;
            var tall = SlideTall - Margin - 1400000 - Margin;

            shapes.Append(CultureInfo.InvariantCulture, $"""<p:pic><p:nvPicPr><p:cNvPr id="{id++}" name="Picture"/><p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed="rId2"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x="{left}" y="{Margin + 1400000}"/><a:ext cx="{wide}" cy="{tall}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr></p:pic>""");
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val="{Hex(look.Ground)}"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>{shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>
            """;
    }

    private static string TextShape(
        int id, string name, long x, long y, long wide, long tall,
        IReadOnlyList<string> lines, int size, bool bold, string colour)
    {
        var paragraphs = new StringBuilder();

        foreach (var line in lines)
        {
            paragraphs.Append(CultureInfo.InvariantCulture, $"""<a:p><a:r><a:rPr lang="en" sz="{size}" b="{(bold ? 1 : 0)}" dirty="0"><a:solidFill><a:srgbClr val="{Hex(colour)}"/></a:solidFill></a:rPr><a:t>{Escape(line)}</a:t></a:r></a:p>""");
        }

        return $"""
            <p:sp><p:nvSpPr><p:cNvPr id="{id}" name="{name}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{wide}" cy="{tall}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:noFill/></p:spPr><p:txBody><a:bodyPr wrap="square"><a:normAutofit/></a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>
            """;
    }

    /// <summary>The first picture on a slide, decoded, or null.</summary>
    private static (byte[] Bytes, string Extension)? PictureIn(DesignDocument document, DesignNode frame)
    {
        foreach (var node in document.ChildrenOf(frame.Id))
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

            try
            {
                var header = src[..comma];

                var extension = header.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                    || header.Contains("jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg"
                    : header.Contains("gif", StringComparison.OrdinalIgnoreCase) ? ".gif"
                    : ".png";

                return (Convert.FromBase64String(src[(comma + 1)..]), extension);
            }
            catch (FormatException)
            {
                // A picture that is not the base64 it claims to be costs the
                // picture, not the deck.
            }
        }

        return null;
    }

    /// <summary>A hex colour with no hash, which is what DrawingML wants.</summary>
    private static string Hex(string colour)
    {
        var trimmed = colour.TrimStart('#').Trim();

        // Three-digit shorthand doubled out, because #fff is a colour somebody
        // may have written and "fff" is not a colour PowerPoint will open.
        if (trimmed.Length == 3)
        {
            trimmed = string.Concat(trimmed.Select(c => $"{c}{c}"));
        }

        return trimmed.Length == 6 && trimmed.All(Uri.IsHexDigit)
            ? trimmed.ToUpperInvariant()
            : "000000";
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
