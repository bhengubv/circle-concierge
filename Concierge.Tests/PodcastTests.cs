using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;

namespace Concierge.Tests;

/// <summary>
/// Podcasts — the one part of Antra's list that works end to end with nothing.
///
/// A feed is RSS on somebody's own server, published so anybody may read it and download the
/// audio: that is what the format is *for*. No account, no key, nothing to work around. So
/// following a show, listing its episodes and keeping one is a complete feature rather than
/// half of one waiting on credentials.
///
/// A feed is somebody else's output, which is where the tests are pointed: namespaces, CDATA,
/// missing dates, items with no audio on them, durations written three different ways.
/// </summary>
public sealed class PodcastTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "concierge-podcasts", Guid.NewGuid().ToString("n"));

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

    private sealed class Answering(string? feed = null, byte[]? audio = null) : IWebAccess
    {
        public string? Asked { get; private set; }

        public Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebFetchResult(true, url, null, string.Empty, 0, false));

        public Task<WebBytesResult> FetchBytesAsync(
            string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default)
        {
            Asked = url;

            if (expectedType.StartsWith("audio", StringComparison.Ordinal))
            {
                return Task.FromResult(audio is null
                    ? new WebBytesResult(false, url, string.Empty, [], "No audio there.")
                    : new WebBytesResult(true, url, "audio/mpeg", audio, null));
            }

            return Task.FromResult(feed is null
                ? new WebBytesResult(false, url, string.Empty, [], "That feed did not answer.")
                : new WebBytesResult(true, url, "application/rss+xml", Encoding.UTF8.GetBytes(feed), null));
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

    /// <summary>A feed shaped like the real ones, awkward parts included.</summary>
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>A Show About Things</title>
            <description><![CDATA[<p>Two people talk about <b>things</b>.</p>]]></description>
            <item>
              <title>The older one</title>
              <pubDate>Mon, 01 Sep 2025 06:00:00 GMT</pubDate>
              <itunes:duration>1800</itunes:duration>
              <enclosure url="https://example.test/one.mp3" type="audio/mpeg" length="1000"/>
            </item>
            <item>
              <title>The newer one</title>
              <pubDate>Wed, 10 Sep 2025 06:00:00 GMT</pubDate>
              <itunes:duration>01:02:03</itunes:duration>
              <enclosure url="https://example.test/two.mp3" type="audio/mpeg" length="2000"/>
            </item>
            <item>
              <title>A written post with no audio</title>
              <pubDate>Thu, 11 Sep 2025 06:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    // ── Reading a feed ────────────────────────────────────────────────────

    [Fact]
    public async Task A_show_and_its_episodes_come_back()
    {
        var (show, problem) = await new Podcasts(new Answering(Feed)).ReadAsync("https://example.test/feed");

        Assert.Null(problem);
        Assert.Equal("A Show About Things", show!.Title);
        Assert.Equal(2, show.Episodes.Count);
    }

    /// <summary>
    /// Feeds are usually newest-first already and are not always, so it is done here rather
    /// than trusted.
    /// </summary>
    [Fact]
    public async Task Newest_first_whatever_order_the_feed_was_in()
    {
        var (show, _) = await new Podcasts(new Answering(Feed)).ReadAsync("https://example.test/feed");

        Assert.Equal("The newer one", show!.Episodes[0].Title);
    }

    /// <summary>
    /// An item with no audio on it is a blog post in a podcast feed, which happens. Listing it
    /// would offer somebody an episode that cannot be played.
    /// </summary>
    [Fact]
    public async Task A_post_with_no_audio_is_not_offered_as_an_episode()
    {
        var (show, _) = await new Podcasts(new Answering(Feed)).ReadAsync("https://example.test/feed");

        Assert.DoesNotContain(show!.Episodes, episode => episode.Title.Contains("written post"));
    }

    /// <summary>Feeds write a duration as seconds, as mm:ss, or as hh:mm:ss.</summary>
    [Theory]
    [InlineData("1800", 1800)]
    [InlineData("30:00", 1800)]
    [InlineData("01:02:03", 3723)]
    [InlineData("", 0)]
    [InlineData("half an hour", 0)]
    public void However_long_it_is_written_the_length_comes_out_in_seconds(string written, double seconds)
        => Assert.Equal(seconds, Podcasts.Length(written));

    /// <summary>
    /// Feeds carry whole HTML pages in the description, and a model handed forty of those has
    /// spent its context on somebody's advertising.
    /// </summary>
    [Fact]
    public async Task The_markup_in_a_description_does_not_come_with_it()
    {
        var (show, _) = await new Podcasts(new Answering(Feed)).ReadAsync("https://example.test/feed");

        Assert.DoesNotContain("<p>", show!.About, StringComparison.Ordinal);
        Assert.Contains("Two people talk about things", show.About, StringComparison.Ordinal);
    }

    /// <summary>
    /// The commonest cause of this is a page that is not a feed, so the address is in the
    /// message rather than a bare parser error.
    /// </summary>
    [Fact]
    public async Task Something_that_is_not_a_feed_says_so_with_its_address()
    {
        var (show, problem) = await new Podcasts(new Answering("<html><body>Hello</body></html>"))
            .ReadAsync("https://example.test/not-a-feed");

        Assert.Null(show);
        Assert.Contains("not-a-feed", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_feed_that_does_not_answer_says_so()
        => Assert.NotNull((await new Podcasts(new Answering()).ReadAsync("https://example.test/feed")).Problem);

    // ── Following shows ───────────────────────────────────────────────────

    private PodcastFollows Following() => new(Path.Combine(_folder, "podcasts.json"));

    [Fact]
    public void A_show_can_be_followed_and_stays_followed()
    {
        var follows = Following();

        Assert.True(follows.Follow("https://example.test/feed"));
        Assert.Equal(["https://example.test/feed"], Following().All());
    }

    [Fact]
    public void Following_the_same_show_twice_is_following_it_once()
    {
        var follows = Following();

        follows.Follow("https://example.test/feed");
        follows.Follow("https://EXAMPLE.test/feed");

        Assert.Single(follows.All());
    }

    [Fact]
    public void And_it_can_be_dropped_again()
    {
        var follows = Following();

        follows.Follow("https://example.test/feed");
        follows.Unfollow("https://example.test/feed");

        Assert.Empty(Following().All());
    }

    [Fact]
    public void A_broken_list_is_an_empty_one_rather_than_a_crash()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "podcasts.json"), "{ not json");

        Assert.Empty(Following().All());
    }

    [Fact]
    public void A_head_with_nowhere_to_keep_a_list_says_so_rather_than_pretending()
    {
        var nowhere = new PodcastFollows();

        Assert.False(nowhere.Follow("https://example.test/feed"));
        Assert.Empty(nowhere.All());
    }

    // ── As tools ──────────────────────────────────────────────────────────

    private IAgentTool? Tool(string name, IWebAccess? web, IToolApprovalService? approval)
        => new PodcastToolSource(web, approval, Following(), _folder).Tools
            .SingleOrDefault(tool => tool.Name == name);

    [Fact]
    public void With_no_way_to_reach_the_network_or_to_ask_none_are_offered()
    {
        Assert.Empty(new PodcastToolSource(new Answering(Feed), null).Tools);
        Assert.Empty(new PodcastToolSource(null, new Answers(ToolApprovalDecision.Allowed)).Tools);
    }

    [Fact]
    public async Task Reading_a_feed_asks_first_with_the_address_on_the_card()
    {
        var approver = new Answers(ToolApprovalDecision.Denied);

        var result = await Tool("podcast_episodes", new Answering(Feed), approver)!
            .InvokeAsync(new JsonObject { ["feed"] = "https://example.test/feed" });

        Assert.False(result.Success);
        Assert.Equal("https://example.test/feed", approver.Asked!.Summary);
    }

    [Fact]
    public async Task The_episodes_come_back_dated_and_named()
    {
        var result = await Tool("podcast_episodes", new Answering(Feed),
                new Answers(ToolApprovalDecision.Allowed))!
            .InvokeAsync(new JsonObject { ["feed"] = "https://example.test/feed" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Contains("2025-09-10 — The newer one", result.Output, StringComparison.Ordinal);
        Assert.Contains("A Show About Things", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writing a list of addresses on this machine is the same act as writing a note to
    /// yourself, so it does not ask.
    /// </summary>
    [Fact]
    public async Task Following_a_show_does_not_ask_anybody()
    {
        var approver = new Answers(ToolApprovalDecision.Denied);

        var result = await Tool("podcast_follow", new Answering(Feed), approver)!
            .InvokeAsync(new JsonObject { ["feed"] = "https://example.test/feed" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Null(approver.Asked);
    }

    [Fact]
    public async Task Something_that_is_not_an_address_is_not_followed()
    {
        var result = await Tool("podcast_follow", new Answering(Feed),
                new Answers(ToolApprovalDecision.Allowed))!
            .InvokeAsync(new JsonObject { ["feed"] = "my favourite show" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task An_episode_is_kept_where_it_can_be_found()
    {
        var result = await Tool("podcast_keep", new Answering(Feed, [1, 2, 3, 4]),
                new Answers(ToolApprovalDecision.Allowed))!
            .InvokeAsync(new JsonObject
            {
                ["url"] = "https://example.test/two.mp3",
                ["name"] = "The newer one",
            });

        Assert.True(result.Success, result.FailureMessage);
        Assert.True(File.Exists(Path.Combine(_folder, "The newer one.mp3")));
    }

    [Fact]
    public async Task Refused_means_nothing_was_downloaded()
    {
        var result = await Tool("podcast_keep", new Answering(Feed, [1, 2, 3, 4]),
                new Answers(ToolApprovalDecision.Denied))!
            .InvokeAsync(new JsonObject { ["url"] = "https://example.test/two.mp3" });

        Assert.False(result.Success);
        Assert.Empty(Directory.Exists(_folder)
            ? Directory.GetFiles(_folder, "*.mp3")
            : []);
    }
}
