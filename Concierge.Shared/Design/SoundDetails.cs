namespace Concierge.Shared.Design;

/// <summary>
/// What a piece of music is, beyond the sound of it.
///
/// A running order that exports to one file and nothing else was half of what
/// somebody means by "make me a thing to listen to". The other half is that the
/// file knows what it is when it lands on a phone: a title, who made it, what it
/// belongs to, a picture, and the words.
///
/// Taken from Antra, whose whole subject this is. Its half was written off here as
/// "a library, not a design surface" — my decision, in my own commit, presented
/// afterwards as though it had been agreed. It had not been, and the line between
/// the two halves was never as clean as that sentence made it sound: artwork and
/// lyrics are things you look at, and a surface that shows a track with neither
/// is showing you less than the file already holds.
///
/// **What is still not here, and it is one thing:** fetching. Antra takes links
/// in — you give it an address and it goes and gets the music. Every source here
/// is something already on the machine, because the export refuses to make a
/// network request to a string a model may have written, and that refusal is
/// deliberate and load-bearing. Turning it on is a decision about what this
/// program is allowed to reach, which is not mine to take quietly.
///
/// Read off properties rather than a new node shape, the same way
/// <see cref="DesignTiming"/> is, so nothing migrates and a track carrying none of
/// this behaves exactly as it did.
/// </summary>
/// <param name="Title">What the piece is called.</param>
/// <param name="Artist">Who made it.</param>
/// <param name="Album">What it belongs to, if anything.</param>
/// <param name="Year">When, as a plain year. Empty when not said.</param>
/// <param name="Artwork">A picture, as a data URI, or null.</param>
/// <param name="Lyrics">The words, or empty.</param>
public sealed record SoundDetails(
    string Title,
    string Artist,
    string Album,
    string Year,
    string? Artwork,
    string Lyrics)
{
    /// <summary>What a track says about itself.</summary>
    /// <param name="node">The track.</param>
    /// <param name="document">
    /// The design it sits in. An album name and a picture said once cover
    /// everything in the running order — a person naming the record twelve times
    /// is a person using a form, which is the thing this surface is not.
    /// </param>
    public static SoundDetails Of(DesignNode node, DesignDocument? document = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        var whole = document is null ? null : document.Find(document.RootId);

        return new SoundDetails(
            Title: Text(node, "title") is { Length: > 0 } named ? named
                : node.Text.Length > 0 ? node.Text : string.Empty,
            Artist: Either(node, whole, "artist"),
            Album: Either(node, whole, "album"),
            Year: FourDigits(Either(node, whole, "year")),
            Artwork: Picture(Text(node, "artwork")) ?? Picture(whole is null ? null : Text(whole, "artwork")),
            Lyrics: Text(node, "lyrics"));
    }

    /// <summary>Whether there is anything here worth writing into a file.</summary>
    public bool Anything =>
        Title.Length > 0 || Artist.Length > 0 || Album.Length > 0
        || Year.Length > 0 || Lyrics.Length > 0;

    /// <summary>
    /// Whether there is anything to show that is not already on screen.
    ///
    /// Separate from <see cref="Anything"/> on purpose, and the difference is a
    /// title. A title belongs in the file, and it is already the caption above the
    /// player — so a track that knows only its own name has nothing to add, and
    /// asking the wrong one of these drew an empty box under every track in the
    /// running order.
    /// </summary>
    public bool WorthShowing =>
        Artist.Length > 0 || Album.Length > 0 || Year.Length > 0
        || Lyrics.Length > 0 || Artwork is not null;

    /// <summary>
    /// The track's own answer, or the design's, or nothing. Asked in that order
    /// because a track that names its own artist is naming a guest.
    /// </summary>
    private static string Either(DesignNode node, DesignNode? whole, string key)
        => Text(node, key) is { Length: > 0 } mine ? mine
            : whole is null ? string.Empty : Text(whole, key);

    private static string Text(DesignNode node, string key)
        => node.Props.TryGetValue(key, out var value) && value is not null ? value.Trim() : string.Empty;

    /// <summary>
    /// A year, or nothing.
    ///
    /// Four digits taken out of whatever was said, because "1997", "in 1997" and
    /// "1997-03-04" all mean the same thing to somebody typing, and a tag that
    /// reads "in 1997" is a tag a music player shows verbatim.
    /// </summary>
    private static string FourDigits(string said)
    {
        for (var at = 0; at + 4 <= said.Length; at++)
        {
            var run = said.AsSpan(at, 4);

            if (run[0] is '1' or '2' && char.IsAsciiDigit(run[1])
                && char.IsAsciiDigit(run[2]) && char.IsAsciiDigit(run[3]))
            {
                return run.ToString();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// A picture, only if it is one carried in the design itself.
    ///
    /// The same rule the paperclip already follows: a data URI travels with the
    /// document, and an address does not. A cover that lives on somebody's machine
    /// or somewhere on the web makes a design that looks complete here and arrives
    /// somewhere else with a hole in it.
    /// </summary>
    private static string? Picture(string? source)
        => source is { Length: > 0 }
            && source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            && source.Contains(";base64,", StringComparison.OrdinalIgnoreCase)
                ? source
                : null;
}
