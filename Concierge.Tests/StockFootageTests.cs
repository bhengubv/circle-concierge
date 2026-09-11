using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;

namespace Concierge.Tests;

/// <summary>
/// Footage anybody may actually use.
///
/// OpenMontage ships stock footage providers, and every archive people reach for first —
/// Pexels, Pixabay, Storyblocks — wants an account and a key, which is one of the things this
/// repository has recorded as missing. Wikimedia Commons wants neither and writes a licence
/// beside everything in it.
///
/// **The licence filter is the part that matters and the part that can be quietly wrong.** A
/// clip somebody puts in a film and then cannot show is worse than no clip at all, so the
/// answers this gives are tested against the archive's real shape rather than against a
/// hopeful one.
/// </summary>
public sealed class StockFootageTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "concierge-footage", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A web that answers with whatever it was given, and remembers what was asked.</summary>
    private sealed class Answering(string json, byte[]? bytes = null) : IWebAccess
    {
        public string? Asked { get; private set; }

        public Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebFetchResult(true, url, null, json, json.Length, false));

        public Task<WebBytesResult> FetchBytesAsync(
            string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default)
        {
            Asked = url;

            return Task.FromResult(expectedType.StartsWith("application/json", StringComparison.Ordinal)
                ? new WebBytesResult(true, url, "application/json", Encoding.UTF8.GetBytes(json), null)
                : new WebBytesResult(true, url, "video/webm", bytes ?? [1, 2, 3, 4], null));
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

    /// <summary>
    /// The archive's real answer, trimmed. Taken from an actual call rather than invented,
    /// because a parser tested against a shape somebody imagined is a parser that works until
    /// it is used.
    /// </summary>
    private static string Real(string licence = "cc-by-sa-4.0")
        => """
        {"batchcomplete":"","query":{"pages":{"122421094":{
          "pageid":122421094,"ns":6,"title":"File:Water waves in Herzliya beach.webm",
          "imageinfo":[{"size":20622129,"width":1920,"height":1080,"duration":12.563,
            "url":"https://upload.wikimedia.org/wikipedia/commons/0/09/Water_waves.webm",
            "descriptionurl":"https://commons.wikimedia.org/wiki/File:Water_waves.webm",
            "extmetadata":{
              "Artist":{"value":"<a href=\"//commons.wikimedia.org/wiki/User:Someone\" title=\"User:Someone\">A Person</a>"},
              "LicenseShortName":{"value":"CC BY-SA 4.0"},
              "License":{"value":"LICENCE"}},
            "mime":"video/webm"}]}}}}
        """.Replace("LICENCE", licence, StringComparison.Ordinal);

    // ── Which licences are free ───────────────────────────────────────────

    [Theory]
    [InlineData("cc-by-4.0")]
    [InlineData("cc-by-sa-3.0")]
    [InlineData("cc-zero")]
    [InlineData("pd-usgov")]
    [InlineData("public domain")]
    public void A_licence_that_lets_somebody_use_and_change_it_passes(string licence)
        => Assert.True(StockFootage.Free(licence));

    /// <summary>
    /// "cc-by-nc-4.0" starts with "cc-by" and is not free to use in anything anybody sells;
    /// "cc-by-nd" forbids the cutting this whole medium is for. Both look right at a glance,
    /// which is exactly why they are checked before the prefix is.
    /// </summary>
    [Theory]
    [InlineData("cc-by-nc-4.0")]
    [InlineData("cc-by-nc-sa-3.0")]
    [InlineData("cc-by-nd-4.0")]
    [InlineData("")]
    [InlineData("fair use")]
    public void One_that_does_not_is_refused(string licence)
        => Assert.False(StockFootage.Free(licence));

    // ── Reading the archive's answer ──────────────────────────────────────

    [Fact]
    public async Task A_freely_licensed_clip_comes_back_with_everything_needed_to_use_it()
    {
        var (clips, problem) = await new StockFootage(new Answering(Real())).SearchAsync("waves");

        Assert.Null(problem);

        var clip = Assert.Single(clips);

        Assert.Equal("https://upload.wikimedia.org/wikipedia/commons/0/09/Water_waves.webm", clip.Url);
        Assert.Equal("CC BY-SA 4.0", clip.Licence);
        Assert.Equal(12.563, clip.Seconds, 3);
        Assert.Equal("video/webm", clip.MediaType);
    }

    /// <summary>
    /// Nearly every free licence here requires attribution, and a result that omits the author
    /// quietly sets somebody up to breach it. The archive returns an anchor tag, and a credit
    /// reading &lt;a href=…&gt;Someone&lt;/a&gt; on a film is worse than no credit.
    /// </summary>
    [Fact]
    public async Task Who_made_it_comes_back_with_it_and_without_the_markup()
    {
        var (clips, _) = await new StockFootage(new Answering(Real())).SearchAsync("waves");

        Assert.Equal("A Person", clips[0].By);
    }

    [Fact]
    public async Task A_clip_nobody_may_change_never_appears_at_all()
    {
        var (clips, problem) = await new StockFootage(new Answering(Real("cc-by-nc-4.0"))).SearchAsync("waves");

        Assert.Null(problem);
        Assert.Empty(clips);
    }

    [Fact]
    public async Task An_archive_that_answers_with_nonsense_costs_the_search_and_not_the_app()
    {
        var (clips, problem) = await new StockFootage(new Answering("{ not json")).SearchAsync("waves");

        Assert.Empty(clips);
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task With_nothing_asked_for_it_asks_what_the_footage_should_be_of()
        => Assert.NotNull((await new StockFootage(new Answering(Real())).SearchAsync(" ")).Problem);

    [Fact]
    public async Task It_only_asks_the_archive_for_video()
    {
        var web = new Answering(Real());

        await new StockFootage(web).SearchAsync("a city at night");

        Assert.Contains("filetype%3Avideo", web.Asked!, StringComparison.Ordinal);
        Assert.Contains("commons.wikimedia.org", web.Asked, StringComparison.Ordinal);
    }

    // ── As tools ──────────────────────────────────────────────────────────

    private static IAgentTool? Tool(
        string name, IWebAccess? web, IToolApprovalService? approval, string? into = null)
        => new StockFootageToolSource(web, approval, into).Tools.SingleOrDefault(tool => tool.Name == name);

    [Fact]
    public void With_no_way_to_reach_the_network_or_to_ask_neither_is_offered()
    {
        Assert.Empty(new StockFootageToolSource(new Answering(Real()), null).Tools);
        Assert.Empty(new StockFootageToolSource(null, new Answers(ToolApprovalDecision.Allowed)).Tools);
    }

    /// <summary>
    /// Going back is free on a canvas and not free on the internet: the request happened, and
    /// whoever is at the other end knows it. So the words being searched for are on the card.
    /// </summary>
    [Fact]
    public async Task Searching_asks_first_with_the_words_on_the_card()
    {
        var approver = new Answers(ToolApprovalDecision.Denied);

        var result = await Tool("stock_footage", new Answering(Real()), approver)!
            .InvokeAsync(new JsonObject { ["of"] = "a city at night" });

        Assert.False(result.Success);
        Assert.Contains("a city at night", approver.Asked!.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Nothing found" and "everything found was licensed in a way you cannot use" are
    /// different answers, and only one of them means try different words.
    /// </summary>
    [Fact]
    public async Task Nothing_usable_says_both_of_the_reasons_it_could_be()
    {
        var result = await Tool("stock_footage", new Answering(Real("cc-by-nd-4.0")),
                new Answers(ToolApprovalDecision.Allowed))!
            .InvokeAsync(new JsonObject { ["of"] = "waves" });

        Assert.True(result.Success);
        Assert.Contains("cannot be used or changed", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clip_is_brought_down_with_its_licence_written_beside_it()
    {
        var result = await Tool("stock_fetch", new Answering(Real(), [1, 2, 3, 4]),
                new Answers(ToolApprovalDecision.Allowed), _folder)!
            .InvokeAsync(new JsonObject
            {
                ["url"] = "https://upload.wikimedia.org/wikipedia/commons/0/09/Water_waves.webm",
                ["licence"] = "CC BY-SA 4.0",
                ["by"] = "A Person",
            });

        Assert.True(result.Success, result.FailureMessage);

        var clip = Path.Combine(_folder, "Water_waves.webm");

        Assert.True(File.Exists(clip));

        // A file on a disk six months later says nothing about who made it or what may be
        // done with it, and the moment somebody needs that is the moment they publish.
        var licence = await File.ReadAllTextAsync(clip + ".licence.txt");

        Assert.Contains("CC BY-SA 4.0", licence, StringComparison.Ordinal);
        Assert.Contains("A Person", licence, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model can write any string into this, and the whole promise of the search is that
    /// nothing unusable comes through it. So the licence is checked again here.
    /// </summary>
    [Fact]
    public async Task A_clip_with_a_licence_nobody_may_use_is_refused_even_if_asked_for_directly()
    {
        var approver = new Answers(ToolApprovalDecision.Allowed);

        var result = await Tool("stock_fetch", new Answering(Real(), [1, 2, 3, 4]), approver, _folder)!
            .InvokeAsync(new JsonObject
            {
                ["url"] = "https://upload.wikimedia.org/wikipedia/commons/0/09/Water_waves.webm",
                ["licence"] = "cc-by-nc-4.0",
            });

        Assert.False(result.Success);
        Assert.Null(approver.Asked);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task Refused_means_nothing_was_downloaded()
    {
        var result = await Tool("stock_fetch", new Answering(Real(), [1, 2, 3, 4]),
                new Answers(ToolApprovalDecision.Denied), _folder)!
            .InvokeAsync(new JsonObject
            {
                ["url"] = "https://upload.wikimedia.org/wikipedia/commons/0/09/Water_waves.webm",
                ["licence"] = "CC BY 4.0",
            });

        Assert.False(result.Success);
        Assert.False(Directory.Exists(_folder));
    }
}
