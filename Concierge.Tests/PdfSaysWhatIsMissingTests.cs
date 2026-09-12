using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// A PDF that left pictures out has to say so.
///
/// The same defect as the running order that exported three tracks out of six, one file over.
/// A PDF carries a JPEG exactly as it is; a PNG needs the encoder to convert it, and without
/// one — or when the conversion fails, or the bytes are not really a picture — it is dropped.
///
/// **Dropping it is right.** Bytes a reader renders as noise are worse than a gap. Saying
/// nothing about it is not: the answer was "Saved to …\deck.pdf" either way, and somebody
/// sends that to a client and finds out from the client.
///
/// Found by going looking for the same shape after the audio one, rather than by waiting for
/// it to happen.
/// </summary>
public sealed class PdfSaysWhatIsMissingTests
{
    private const string APng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static DesignDocument APageWithAPicture()
    {
        var page = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Sports Day"));

        return DesignDocument.Blank(medium: DesignMedium.Deck)
            .Add(page)
            .Add(DesignNode.New(DesignNodeKind.Image, page.Id, ("text", "The field"), ("src", APng)));
    }

    private static string Somewhere() => Path.Combine(
        Path.GetTempPath(), $"pdf-{Guid.NewGuid():N}", "deck.pdf");

    /// <summary>
    /// With no encoder a PNG cannot become a JPEG, so it is left out — and said.
    /// </summary>
    [Fact]
    public async Task A_picture_that_could_not_be_carried_is_named()
    {
        var wrote = await PdfExport.WriteAsync(APageWithAPicture(), Somewhere(), encoder: null);

        Assert.True(wrote.Ok, wrote.Problem);
        Assert.False(string.IsNullOrWhiteSpace(wrote.Left), "a picture was dropped and nothing said so");
        Assert.Contains("1 picture", wrote.Left!, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the reason is the one that is true here: no encoder, rather than a conversion that
    /// went wrong. Somebody who installs ffmpeg on that advice has fixed their own problem.
    /// </summary>
    [Fact]
    public async Task And_says_why_rather_than_only_that()
    {
        var wrote = await PdfExport.WriteAsync(APageWithAPicture(), Somewhere(), encoder: null);

        Assert.Contains("no encoder", wrote.Left!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A page with no pictures on it says nothing, so the warning keeps its meaning.
    /// </summary>
    [Fact]
    public async Task But_a_deck_with_no_pictures_says_nothing()
    {
        var page = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Sports Day"));

        var plain = DesignDocument.Blank(medium: DesignMedium.Deck)
            .Add(page)
            .Add(DesignNode.New(DesignNodeKind.Heading, page.Id, ("text", "Saturday")));

        var wrote = await PdfExport.WriteAsync(plain, Somewhere(), encoder: null);

        Assert.True(wrote.Ok, wrote.Problem);
        Assert.Null(wrote.Left);
    }

    /// <summary>
    /// And the file is still written. Leaving a picture out must not cost the document —
    /// words, colours and order are most of what a PDF is for.
    /// </summary>
    [Fact]
    public async Task And_the_pdf_is_still_written()
    {
        var where = Somewhere();

        var wrote = await PdfExport.WriteAsync(APageWithAPicture(), where, encoder: null);

        Assert.True(File.Exists(where), "no file was written");
        Assert.True(new FileInfo(where).Length > 0, "the file is empty");
        Assert.Equal(where, wrote.Path);
    }

    /// <summary>
    /// Bytes that are not a picture at all count too — every way out of the search leaves the
    /// picture out of the file, so every one of them has to be counted. The first version of
    /// this counted three of the five and would have gone quiet on the other two.
    /// </summary>
    [Fact]
    public async Task And_bytes_that_are_not_a_picture_count_as_missing()
    {
        var page = DesignNode.New(DesignNodeKind.Frame, null, ("text", "Sports Day"));

        var rubbish = DesignDocument.Blank(medium: DesignMedium.Deck)
            .Add(page)
            .Add(DesignNode.New(
                DesignNodeKind.Image, page.Id, ("text", "Not a picture"),
                ("src", "data:image/jpeg;base64,bm90IGEgcGljdHVyZQ==")));

        var wrote = await PdfExport.WriteAsync(rubbish, Somewhere(), encoder: null);

        Assert.True(wrote.Ok, wrote.Problem);
        Assert.False(string.IsNullOrWhiteSpace(wrote.Left), "unusable bytes were dropped in silence");
    }
}
