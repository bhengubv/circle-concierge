using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// What the history strip carries.
///
/// **Measured on the running app: 2,086 KB of markup across three moments** — about 700 KB
/// each — for a design carrying 390 KB of sound. Thirty moments is the limit, and the import
/// budget allows eight megabytes of audio, so a strip could hold something like three hundred
/// megabytes of string.
///
/// That cost arrived the morning audio started travelling inside the design, and nothing else
/// changed to meet it. A thumbnail is sixty-four pixels drawn at eight per cent: there is no
/// player to see in one and nothing to hear, so the audio was pure weight.
///
/// Pictures stay, because they are the only thing that makes a thumbnail recognisable — which
/// is the entire reason the strip is pictures rather than a list of sentences.
/// </summary>
public sealed class HistoryStripWeightTests
{
    private const string ACarriedTrack =
        "data:audio/mpeg;base64,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private const string APicture =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static DesignSession AMomentWith(params (string Kind, string Src)[] things)
        => AMomentIn(DesignMedium.Sound, things);

    /// <summary>
    /// A medium is named because it decides what gets drawn at all: a picture in a running
    /// order is not drawn by anything, which the first version of the picture test below
    /// discovered by failing.
    /// </summary>
    private static DesignSession AMomentIn(DesignMedium medium, params (string Kind, string Src)[] things)
    {
        var session = new DesignSession(medium: medium);
        var document = session.Current;

        foreach (var (kind, src) in things)
        {
            document = document.Add(kind == "sound"
                ? DesignNode.New(DesignNodeKind.Sound, null, ("text", "A track"), ("src", src))
                : DesignNode.New(DesignNodeKind.Image, null, ("text", "A picture"), ("src", src)));
        }

        session.Record(document, "Brought in");

        return session;
    }

    /// <summary>
    /// The audio is not in the thumbnail.
    /// </summary>
    [Fact]
    public void A_thumbnail_does_not_carry_the_audio()
    {
        var session = AMomentWith(("sound", ACarriedTrack));

        var thumbnail = session.HtmlAt(session.Position);

        Assert.DoesNotContain(ACarriedTrack, thumbnail, StringComparison.Ordinal);
    }

    /// <summary>
    /// But the canvas itself still has it, which is the whole point of carrying it.
    /// </summary>
    [Fact]
    public void But_the_canvas_itself_still_does()
        => Assert.Contains(
            ACarriedTrack,
            AMomentWith(("sound", ACarriedTrack)).Html(),
            StringComparison.Ordinal);

    /// <summary>
    /// **Pictures stay.** Dropping those too would be the cheaper change and the wrong one:
    /// a strip of blank rectangles is a list of sentences with extra steps.
    /// </summary>
    [Fact]
    public void And_pictures_are_still_in_the_thumbnail()
    {
        var session = AMomentIn(DesignMedium.Page, ("picture", APicture));

        Assert.Contains(APicture, session.HtmlAt(session.Position), StringComparison.Ordinal);
    }

    /// <summary>
    /// The track is still named in the thumbnail, rather than disappearing from it — the
    /// renderer already draws a source-less track as a named placeholder, so a thumbnail shows
    /// a running order that looks like a running order.
    /// </summary>
    [Fact]
    public void And_the_track_is_still_named_in_it()
        => Assert.Contains(
            "A track",
            AMomentWith(("sound", ACarriedTrack)).HtmlAt(0 + 1),
            StringComparison.Ordinal);

    /// <summary>
    /// And it is meaningfully smaller, which is the reason any of this was done.
    /// </summary>
    [Fact]
    public void And_the_thumbnail_is_smaller_than_the_canvas()
    {
        var session = AMomentWith(("sound", ACarriedTrack));

        Assert.True(
            session.HtmlAt(session.Position).Length < session.Html().Length,
            "the thumbnail is no lighter than the canvas");
    }

    /// <summary>
    /// A design with no carried audio is handed back untouched rather than rebuilt — the
    /// common case, and rebuilding a document to change nothing is work for nobody.
    /// </summary>
    [Fact]
    public void And_a_design_with_nothing_carried_is_left_alone()
    {
        var session = AMomentIn(DesignMedium.Page, ("picture", APicture));

        Assert.Equal(
            DesignMediums.Render(session.Current),
            session.HtmlAt(session.Position));
    }
}
