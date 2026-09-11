using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;

namespace Concierge.Tests;

/// <summary>
/// Asking the world's catalogues about a recording.
///
/// **Antra's identification half, and it needs no account.** Matching the exact recording
/// rather than the right title, choosing between clean and explicit, and listing a discography
/// are lookups, and MusicBrainz, Deezer and Apple are all open to anybody.
///
/// The answers below are the real shape of those services, taken from actual calls. A parser
/// tested against a shape somebody imagined is a parser that works until it is used.
/// </summary>
public sealed class MusicCatalogueTests
{
    /// <summary>A web that answers each catalogue with whatever it was given.</summary>
    private sealed class Answering(string? deezer = null, string? apple = null, string? brainz = null)
        : IWebAccess
    {
        public List<string> Asked { get; } = [];

        public Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebFetchResult(true, url, null, string.Empty, 0, false));

        public Task<WebBytesResult> FetchBytesAsync(
            string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default)
        {
            Asked.Add(url);

            var body =
                url.Contains("deezer", StringComparison.Ordinal) ? deezer
                : url.Contains("itunes", StringComparison.Ordinal) ? apple
                : url.Contains("musicbrainz", StringComparison.Ordinal) ? brainz
                : null;

            return Task.FromResult(body is null
                ? new WebBytesResult(false, url, string.Empty, [], "That catalogue did not answer.")
                : new WebBytesResult(true, url, "application/json", Encoding.UTF8.GetBytes(body), null));
        }
    }

    private sealed class Answers(ToolApprovalDecision decision) : IToolApprovalService
    {
        public ToolApprovalRequest? Asked { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Asked = request;
            return ValueTask.FromResult(decision);
        }
    }

    /// <summary>Deezer's real answer, trimmed. It is the one that carries the ISRC.</summary>
    private const string Deezer = """
        {"data":[{"id":709830252,"title":"Sinnerman","isrc":"USPR36500095",
          "link":"https://www.deezer.com/track/709830252","duration":619,
          "explicit_lyrics":false,
          "artist":{"id":744,"name":"Nina Simone"},
          "album":{"id":1,"title":"Pastel Blues"}}],"total":1}
        """;

    /// <summary>Apple's real answer, trimmed. It carries explicitness and no ISRC.</summary>
    private const string Apple = """
        {"resultCount":1,"results":[{"wrapperType":"track","kind":"song",
          "artistName":"Nina Simone","trackName":"Sinnerman",
          "collectionName":"Pastel Blues","trackExplicitness":"notExplicit",
          "trackTimeMillis":619000,"releaseDate":"1965-01-01T08:00:00Z",
          "trackViewUrl":"https://music.apple.com/us/album/sinnerman/1"}]}
        """;

    private const string Brainz = """
        {"release-groups":[
          {"title":"Pastel Blues","first-release-date":"1965-09","primary-type":"Album"},
          {"title":"Wild Is the Wind","first-release-date":"1966","primary-type":"Album"},
          {"title":"A Single","first-release-date":"","primary-type":"Single"}]}
        """;

    // ── What the catalogues say ───────────────────────────────────────────

    [Fact]
    public async Task Both_catalogues_are_asked_and_both_answers_come_back()
    {
        var web = new Answering(Deezer, Apple);

        var found = await new MusicCatalogue(web).FindAsync("Nina Simone Sinnerman");

        Assert.Equal(2, found.Count);
        Assert.Contains(found, recording => recording.Source == "Deezer");
        Assert.Contains(found, recording => recording.Source == "Apple");
        Assert.Contains(web.Asked, url => url.Contains("api.deezer.com", StringComparison.Ordinal));
        Assert.Contains(web.Asked, url => url.Contains("itunes.apple.com", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ISRC is the only thing that identifies a recording exactly, and only one of the two
    /// publishes it here.
    /// </summary>
    [Fact]
    public async Task The_number_that_identifies_it_exactly_comes_back_when_a_catalogue_has_one()
    {
        var found = await new MusicCatalogue(new Answering(Deezer, Apple)).FindAsync("Sinnerman");

        Assert.Equal("USPR36500095", found.Single(recording => recording.Source == "Deezer").Isrc);

        // Apple does not publish it through this endpoint, and it is left empty rather than
        // filled with something that looks like one.
        Assert.Equal(string.Empty, found.Single(recording => recording.Source == "Apple").Isrc);
    }

    /// <summary>
    /// They are separate services having separate afternoons. An answer from one of two is
    /// still an answer.
    /// </summary>
    [Fact]
    public async Task One_catalogue_failing_costs_that_catalogue_and_not_the_answer()
    {
        var found = await new MusicCatalogue(new Answering(deezer: Deezer)).FindAsync("Sinnerman");

        Assert.Single(found);
        Assert.Equal("Deezer", found[0].Source);
    }

    [Fact]
    public async Task A_catalogue_answering_with_nonsense_costs_that_catalogue_too()
        => Assert.Empty(await new MusicCatalogue(new Answering(deezer: "{ not json")).FindAsync("Sinnerman"));

    [Fact]
    public async Task Nothing_asked_for_asks_nobody()
    {
        var web = new Answering(Deezer, Apple);

        Assert.Empty(await new MusicCatalogue(web).FindAsync("   "));
        Assert.Empty(web.Asked);
    }

    // ── Which version ─────────────────────────────────────────────────────

    [Fact]
    public async Task Explicitness_is_read_from_whichever_catalogue_says()
    {
        var found = await new MusicCatalogue(new Answering(Deezer, Apple)).FindAsync("Sinnerman");

        Assert.False(found.Single(recording => recording.Source == "Deezer").Explicit);
        Assert.False(found.Single(recording => recording.Source == "Apple").Explicit);
    }

    /// <summary>
    /// A catalogue that says nothing about explicitness is not evidence that a version is
    /// clean. Treating it as one is how somebody plays the wrong thing to a room.
    /// </summary>
    [Fact]
    public async Task A_catalogue_that_says_nothing_is_not_read_as_clean()
    {
        const string silent = """
            {"data":[{"id":1,"title":"Sinnerman","isrc":"X","link":"","duration":619,
              "artist":{"name":"Nina Simone"},"album":{"title":"Pastel Blues"}}]}
            """;

        var found = await new MusicCatalogue(new Answering(deezer: silent)).FindAsync("Sinnerman");

        Assert.Null(found[0].Explicit);
    }

    // ── What an artist released ───────────────────────────────────────────

    [Fact]
    public async Task A_discography_comes_back_oldest_first()
    {
        var releases = await new MusicCatalogue(new Answering(brainz: Brainz)).DiscographyAsync("Nina Simone");

        Assert.Equal(3, releases.Count);
        Assert.Equal("Pastel Blues", releases[0].Title);
        Assert.Equal(1965, releases[0].Year);

        // One with no date goes last rather than first, which is where a year of nought would
        // otherwise put it.
        Assert.Equal("A Single", releases[^1].Title);
    }

    [Fact]
    public async Task An_artist_nobody_has_heard_of_comes_back_empty()
        => Assert.Empty(await new MusicCatalogue(new Answering()).DiscographyAsync("Nobody At All"));

    // ── What a file actually is ───────────────────────────────────────────

    private static MediaTags Tags(string artist, string title)
        => new(true, title, artist, "Pastel Blues", string.Empty, 1965);

    /// <summary>
    /// "Sinnerman" is a studio take, a live take, a remix and forty compilations. The length
    /// agreeing with a catalogue that also gave a number is what makes an answer exact.
    /// </summary>
    [Fact]
    public async Task A_file_whose_length_agrees_is_matched_exactly()
    {
        var (match, exact, why) = await new MusicCatalogue(new Answering(Deezer, Apple))
            .IdentifyAsync(Tags("Nina Simone", "Sinnerman"), 619);

        Assert.NotNull(match);
        Assert.True(exact, why);
        Assert.Equal("USPR36500095", match!.Isrc);
    }

    /// <summary>
    /// A different take of the same song is the thing this has to catch, and it says so in
    /// words rather than returning a confident wrong answer.
    /// </summary>
    [Fact]
    public async Task A_file_whose_length_disagrees_is_said_to_be_probably_another_take()
    {
        var (match, exact, why) = await new MusicCatalogue(new Answering(Deezer, Apple))
            .IdentifyAsync(Tags("Nina Simone", "Sinnerman"), 345);

        Assert.NotNull(match);
        Assert.False(exact);
        Assert.Contains("different take", why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_says_nothing_about_itself_is_not_guessed_at()
    {
        var (match, exact, _) = await new MusicCatalogue(new Answering(Deezer, Apple))
            .IdentifyAsync(new MediaTags(false, "", "", "", "", 0), 200);

        Assert.Null(match);
        Assert.False(exact);
    }

    [Fact]
    public async Task Nothing_in_any_catalogue_is_said_plainly()
    {
        var (match, _, why) = await new MusicCatalogue(new Answering())
            .IdentifyAsync(Tags("Nobody", "Nothing"), 200);

        Assert.Null(match);
        Assert.Contains("No catalogue", why, StringComparison.Ordinal);
    }

    // ── As tools ──────────────────────────────────────────────────────────

    private static IAgentTool? Tool(string name, IWebAccess? web, IToolApprovalService? approval)
        => new MusicCatalogueToolSource(web, approval).Tools.SingleOrDefault(tool => tool.Name == name);

    [Fact]
    public void With_no_way_to_reach_the_network_or_to_ask_none_are_offered()
    {
        Assert.Empty(new MusicCatalogueToolSource(new Answering(Deezer), null).Tools);
        Assert.Empty(new MusicCatalogueToolSource(null, new Answers(ToolApprovalDecision.Allowed)).Tools);
    }

    [Fact]
    public async Task Looking_something_up_asks_first_with_what_is_being_looked_up()
    {
        var approver = new Answers(ToolApprovalDecision.Denied);

        var result = await Tool("music_find", new Answering(Deezer, Apple), approver)!
            .InvokeAsync(new JsonObject { ["what"] = "Nina Simone Sinnerman" });

        Assert.False(result.Success);
        Assert.Contains("Nina Simone Sinnerman", approver.Asked!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recording_comes_back_described_in_words()
    {
        var result = await Tool("music_find", new Answering(Deezer, Apple),
                new Answers(ToolApprovalDecision.Allowed))!
            .InvokeAsync(new JsonObject { ["what"] = "Nina Simone Sinnerman" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Contains("USPR36500095", result.Output, StringComparison.Ordinal);
        Assert.Contains("clean", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_discography_comes_back_as_years_and_titles()
    {
        var result = await Tool("music_discography", new Answering(brainz: Brainz),
                new Answers(ToolApprovalDecision.Allowed))!
            .InvokeAsync(new JsonObject { ["artist"] = "Nina Simone" });

        Assert.Contains("1965 — Pastel Blues", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_found_is_an_answer_rather_than_a_failure()
    {
        var result = await Tool("music_find", new Answering(), new Answers(ToolApprovalDecision.Allowed))!
            .InvokeAsync(new JsonObject { ["what"] = "Nobody At All" });

        Assert.True(result.Success);
        Assert.Contains("No catalogue", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// None of these downloads any music, and the card says so. Everything up to the download
    /// is here; the file itself comes from somewhere somebody is allowed to take it from.
    /// </summary>
    [Fact]
    public async Task The_card_says_nothing_is_downloaded()
    {
        var approver = new Answers(ToolApprovalDecision.Denied);

        await Tool("music_versions", new Answering(Deezer), approver)!
            .InvokeAsync(new JsonObject { ["what"] = "Sinnerman" });

        Assert.Contains("Nothing is downloaded", approver.Asked!.Detail!, StringComparison.Ordinal);
    }
}
