using Concierge.Shared.Design;

namespace Concierge.Tests;

/// <summary>
/// What a piece of music is, beyond the sound of it.
///
/// Antra's half of Sound — tagged files out, artwork, lyrics — was written off in
/// the task list as "a library, not a design surface". That was my decision, in my
/// own commit, and it was then quoted back as though it had been agreed. It had
/// not been.
///
/// The line was never as clean as the sentence made it sound. Artwork and lyrics
/// are things you look at, and a running order that showed neither was showing
/// less than the file it exported already carried. What is genuinely still out is
/// one thing — fetching music from a link, which is a decision about what this
/// program may reach, not a scoping preference.
/// </summary>
public sealed class SoundDetailsTests
{
    private const string Cover =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static DesignNode Track(params (string Key, string Value)[] props)
        => DesignNode.New(DesignNodeKind.Sound, null, [("text", "Feeling Good"), .. props]);

    // ── What a track knows ────────────────────────────────────────────────

    [Fact]
    public void A_track_with_nothing_said_about_it_has_nothing_to_say()
    {
        var details = SoundDetails.Of(Track());

        Assert.False(details.Anything is false && details.Title.Length == 0);
        Assert.Equal(string.Empty, details.Artist);
        Assert.Equal(string.Empty, details.Album);
        Assert.Null(details.Artwork);
    }

    /// <summary>
    /// The name on screen is the name in the file. Two names for one thing is how
    /// they drift.
    /// </summary>
    [Fact]
    public void A_tracks_title_is_what_it_is_called_on_screen()
        => Assert.Equal("Feeling Good", SoundDetails.Of(Track()).Title);

    [Fact]
    public void A_title_said_outright_wins_over_the_label()
        => Assert.Equal("Sinnerman", SoundDetails.Of(Track(("title", "Sinnerman"))).Title);

    [Fact]
    public void A_track_carries_who_made_it_and_what_it_belongs_to()
    {
        var details = SoundDetails.Of(Track(("artist", "Nina Simone"), ("album", "I Put A Spell On You")));

        Assert.Equal("Nina Simone", details.Artist);
        Assert.Equal("I Put A Spell On You", details.Album);
    }

    /// <summary>
    /// Said once, true of everything. A person naming the record twelve times is a
    /// person filling in a form, which is the thing this surface is not.
    /// </summary>
    [Fact]
    public void What_the_design_says_covers_every_track_in_it()
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Sound);
        document = document.Set(document.RootId, "album", "I Put A Spell On You");

        var track = Track();
        document = document.Add(track);

        Assert.Equal("I Put A Spell On You", SoundDetails.Of(track, document).Album);
    }

    /// <summary>
    /// A track naming its own artist is naming a guest, and the guest wins for
    /// that track only.
    /// </summary>
    [Fact]
    public void A_track_can_disagree_with_the_record_it_is_on()
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Sound);
        document = document.Set(document.RootId, "artist", "Nina Simone");

        var guest = Track(("artist", "Hazel Scott"));
        document = document.Add(guest);

        Assert.Equal("Hazel Scott", SoundDetails.Of(guest, document).Artist);
    }

    /// <summary>
    /// "1965", "in 1965" and "1965-03-04" are one thing to somebody typing, and a
    /// player shows a date tag exactly as it is given.
    /// </summary>
    [Theory]
    [InlineData("1965", "1965")]
    [InlineData("in 1965", "1965")]
    [InlineData("1965-03-04", "1965")]
    [InlineData("released 2011 I think", "2011")]
    [InlineData("sometime in the sixties", "")]
    public void A_year_is_a_year(string said, string expected)
        => Assert.Equal(expected, SoundDetails.Of(Track(("year", said))).Year);

    // ── Artwork, and the rule it follows ──────────────────────────────────

    [Fact]
    public void A_cover_carried_in_the_design_is_kept()
        => Assert.Equal(Cover, SoundDetails.Of(Track(("artwork", Cover))).Artwork);

    /// <summary>
    /// The same rule the paperclip already follows. A cover that lives on
    /// somebody's machine or somewhere on the web makes a design that looks
    /// complete here and arrives somewhere else with a hole in it.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/cover.jpg")]
    [InlineData("file:///C:/Users/me/cover.jpg")]
    [InlineData("cover.jpg")]
    public void A_cover_that_lives_somewhere_else_is_not_a_cover(string source)
        => Assert.Null(SoundDetails.Of(Track(("artwork", source))).Artwork);

    [Fact]
    public void A_cover_on_the_design_covers_a_track_that_has_none()
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Sound);
        document = document.Set(document.RootId, "artwork", Cover);

        var track = Track();
        Assert.Equal(Cover, SoundDetails.Of(track, document.Add(track)).Artwork);
    }

    // ── On screen ─────────────────────────────────────────────────────────

    [Fact]
    public void A_track_shows_who_made_it()
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Sound)
            .Add(Track(("artist", "Nina Simone"), ("album", "Wild Is The Wind"), ("year", "1966")));

        var html = DesignMediums.Render(document);

        Assert.Contains("Nina Simone", html, StringComparison.Ordinal);
        Assert.Contains("Wild Is The Wind", html, StringComparison.Ordinal);
        Assert.Contains("1966", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_track_shows_its_cover()
        => Assert.Contains("class=\"cover\"", DesignMediums.Render(
                DesignDocument.Blank(medium: DesignMedium.Sound).Add(Track(("artwork", Cover)))),
            StringComparison.Ordinal);

    /// <summary>
    /// Folded away. Lyrics are long and this is a running order; printing them
    /// down the page pushes the next track off the screen.
    /// </summary>
    [Fact]
    public void The_words_are_there_and_folded_away()
    {
        var html = DesignMediums.Render(DesignDocument.Blank(medium: DesignMedium.Sound)
            .Add(Track(("lyrics", "Birds flying high\nYou know how I feel"))));

        Assert.Contains("<details class=\"words\">", html, StringComparison.Ordinal);
        Assert.Contains("Birds flying high", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A design that has said none of this looks exactly as it did. An empty field
    /// on screen is a form, and this is not a form.
    /// </summary>
    [Fact]
    public void A_track_that_knows_nothing_grows_no_empty_labels()
        => Assert.DoesNotContain("class=\"about\"", DesignMediums.Render(
                DesignDocument.Blank(medium: DesignMedium.Sound).Add(Track())),
            StringComparison.Ordinal);

    // ── Saying it ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("the artist is Nina Simone", "artist", "Nina Simone")]
    [InlineData("the singer is Nina Simone", "artist", "Nina Simone")]
    [InlineData("the band is Portishead", "artist", "Portishead")]
    [InlineData("the album is called Wild Is The Wind", "album", "Wild Is The Wind")]
    [InlineData("the record is Dummy", "album", "Dummy")]
    [InlineData("the year is 1966", "year", "1966")]
    [InlineData("the words are Birds flying high", "lyrics", "Birds flying high")]
    public void You_can_say_what_a_piece_of_music_is(string said, string key, string expected)
    {
        var document = DesignDocument.Blank(medium: DesignMedium.Sound);
        var heard = DesignSpeech.Hear(document, said, null);

        Assert.True(heard.Understood, said);
        Assert.Equal(expected, heard.Document.Find(heard.Document.RootId)!.Props[key]);
    }

    /// <summary>
    /// Said while pointing at one track, it is about that track. Said while
    /// pointing at nothing, it is about the record — which is what somebody means
    /// when they say the album name once.
    /// </summary>
    [Fact]
    public void Said_while_pointing_it_is_about_that_one()
    {
        var track = Track();
        var document = DesignDocument.Blank(medium: DesignMedium.Sound).Add(track);

        var heard = DesignSpeech.Hear(document, "the artist is Hazel Scott", track.Id);

        Assert.Equal("Hazel Scott", heard.Document.Find(track.Id)!.Props["artist"]);
        Assert.False(heard.Document.Find(document.RootId)!.Props.ContainsKey("artist"));
    }

    /// <summary>
    /// The words are read back as the words, not as a command. "the artist is add
    /// a title" is a strange thing to say and still a thing somebody can say.
    /// </summary>
    [Fact]
    public void What_is_said_is_kept_as_said()
    {
        var heard = DesignSpeech.Hear(
            DesignDocument.Blank(medium: DesignMedium.Sound), "the artist is add a title", null);

        Assert.Equal("add a title", heard.Document.Find(heard.Document.RootId)!.Props["artist"]);
    }
}
