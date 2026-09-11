using System.Text;
using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;

namespace Concierge.Tests;

/// <summary>
/// Looking after a library on a schedule.
///
/// Antra runs downloads on a schedule. **This runs the checking on a schedule and never the
/// downloading**, and that is the decision worth testing: everything here that reaches the
/// network or writes a file asks first, and a scheduled task runs at three in the morning
/// with nobody there to ask.
/// </summary>
public sealed class UpkeepTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "concierge-upkeep", Guid.NewGuid().ToString("n"));

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

    private sealed class Answering(string feed) : IWebAccess
    {
        public int Asked { get; private set; }

        public Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebFetchResult(true, url, null, string.Empty, 0, false));

        public Task<WebBytesResult> FetchBytesAsync(
            string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default)
        {
            Asked++;

            return Task.FromResult(new WebBytesResult(
                true, url, "application/rss+xml", Encoding.UTF8.GetBytes(feed), null));
        }
    }

    private static string FeedWith(DateTimeOffset when) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>A Show</title>
            <item>
              <title>An episode</title>
              <pubDate>{when:R}</pubDate>
              <enclosure url="https://example.test/one.mp3" type="audio/mpeg"/>
            </item>
          </channel>
        </rss>
        """;

    private Upkeep With(IWebAccess? web, params string[] following)
    {
        var follows = new PodcastFollows(Path.Combine(_folder, "podcasts.json"));

        foreach (var feed in following)
        {
            follows.Follow(feed);
        }

        return new Upkeep(Path.Combine(_folder, "upkeep.json"), web, follows);
    }

    // ── What it is told to do ─────────────────────────────────────────────

    [Fact]
    public void It_is_off_until_somebody_turns_it_on()
        => Assert.False(With(null).Plan.On);

    [Fact]
    public void What_it_is_told_is_what_it_remembers()
    {
        var upkeep = With(null);

        Assert.True(upkeep.Tell(new UpkeepPlan(On: true, EveryHours: 6, Folder: _folder)));

        var again = new Upkeep(Path.Combine(_folder, "upkeep.json"));

        Assert.True(again.Plan.On);
        Assert.Equal(6, again.Plan.EveryHours);
    }

    /// <summary>
    /// A mistake must not turn into a program checking somebody's network every few seconds,
    /// and must not turn into one that never runs at all.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(100000, 336)]
    public void How_often_is_bounded_at_both_ends(int asked, int expected)
    {
        var upkeep = With(null);

        upkeep.Tell(new UpkeepPlan(On: true, EveryHours: asked));

        Assert.Equal(expected, upkeep.Plan.EveryHours);
    }

    [Fact]
    public void A_head_with_nowhere_to_keep_a_schedule_says_so()
        => Assert.False(new Upkeep().Tell(new UpkeepPlan(On: true)));

    [Fact]
    public void A_broken_file_is_a_schedule_that_is_off_rather_than_a_crash()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "upkeep.json"), "{ not json");

        var upkeep = new Upkeep(Path.Combine(_folder, "upkeep.json"));

        Assert.False(upkeep.Plan.On);
        Assert.Null(upkeep.Last);
    }

    // ── What a run finds ──────────────────────────────────────────────────

    [Fact]
    public async Task A_new_episode_is_what_it_reports()
    {
        var upkeep = With(new Answering(FeedWith(DateTimeOffset.Now)), "https://example.test/feed");
        upkeep.Tell(new UpkeepPlan(On: true));

        var report = await upkeep.RunAsync();

        Assert.Contains(report.Found, line => line.Contains("A Show: 1 new", StringComparison.Ordinal));
    }

    /// <summary>
    /// "New" means new to somebody who was told about the others yesterday, not new to the
    /// feed.
    /// </summary>
    [Fact]
    public async Task An_episode_already_reported_is_not_reported_again()
    {
        var upkeep = With(new Answering(FeedWith(DateTimeOffset.Now.AddDays(-2))), "https://example.test/feed");
        upkeep.Tell(new UpkeepPlan(On: true));

        await upkeep.RunAsync();
        var second = await upkeep.RunAsync();

        Assert.DoesNotContain(second.Found, line => line.Contains("new", StringComparison.Ordinal));
    }

    /// <summary>
    /// It runs unattended, so a feed having a bad morning is written into the report rather
    /// than thrown at whatever is hosting it.
    /// </summary>
    [Fact]
    public async Task A_feed_that_is_not_a_feed_is_recorded_rather_than_thrown()
    {
        var upkeep = With(new Answering("<html>not a feed</html>"), "https://example.test/feed");
        upkeep.Tell(new UpkeepPlan(On: true));

        var report = await upkeep.RunAsync();

        Assert.Contains(report.Found, line => line.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_web_access_the_shows_are_simply_not_checked()
    {
        var upkeep = With(null, "https://example.test/feed");
        upkeep.Tell(new UpkeepPlan(On: true));

        Assert.Empty((await upkeep.RunAsync()).Found);
    }

    [Fact]
    public async Task What_it_found_is_still_there_afterwards()
    {
        var upkeep = With(new Answering(FeedWith(DateTimeOffset.Now)), "https://example.test/feed");
        upkeep.Tell(new UpkeepPlan(On: true));

        await upkeep.RunAsync();

        Assert.NotNull(new Upkeep(Path.Combine(_folder, "upkeep.json")).Last);
    }

    // ── As a tool ─────────────────────────────────────────────────────────

    private IAgentTool Tool(Upkeep upkeep)
        => new UpkeepToolSource(upkeep).Tools.Single();

    [Fact]
    public async Task Asked_with_nothing_it_says_what_is_being_looked_after()
    {
        var result = await Tool(With(null)).InvokeAsync(new JsonObject());

        Assert.True(result.Success);
        Assert.Contains("schedule is off", result.Output, StringComparison.Ordinal);
        Assert.Contains("has not run yet", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_can_be_turned_on_and_told_how_often()
    {
        var upkeep = With(null);

        var result = await Tool(upkeep).InvokeAsync(
            new JsonObject { ["on"] = true, ["every_hours"] = 6 });

        Assert.True(result.Success, result.FailureMessage);
        Assert.True(upkeep.Plan.On);
        Assert.Equal(6, upkeep.Plan.EveryHours);
        Assert.Contains("every 6 hours", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checked now rather than at three in the morning, when nobody is reading.
    /// </summary>
    [Fact]
    public async Task A_folder_that_is_not_there_is_refused_when_it_is_set()
    {
        var upkeep = With(null);

        var result = await Tool(upkeep).InvokeAsync(
            new JsonObject { ["folder"] = Path.Combine(_folder, "nowhere") });

        Assert.False(result.Success);
        Assert.Equal(string.Empty, upkeep.Plan.Folder);
    }

    [Fact]
    public async Task It_can_be_run_there_and_then()
    {
        var web = new Answering(FeedWith(DateTimeOffset.Now));
        var upkeep = With(web, "https://example.test/feed");

        var result = await Tool(upkeep).InvokeAsync(new JsonObject { ["on"] = true, ["now"] = true });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal(1, web.Asked);
        Assert.Contains("A Show: 1 new", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one thing this must never do. A background job that downloaded would be the only
    /// thing in the product acting with nobody there to agree to it.
    /// </summary>
    [Fact]
    public async Task Nothing_it_does_downloads_or_removes_anything()
    {
        var web = new Answering(FeedWith(DateTimeOffset.Now));
        var upkeep = With(web, "https://example.test/feed");
        upkeep.Tell(new UpkeepPlan(On: true, Folder: _folder));

        await upkeep.RunAsync();

        // One read of the feed and nothing else: no audio fetched, and nothing written into
        // the folder it was watching beyond the two lists it keeps there.
        Assert.Equal(1, web.Asked);
        Assert.Empty(Directory.GetFiles(_folder, "*.mp3"));

        Assert.Contains(
            "never downloads",
            Tool(upkeep).Description,
            StringComparison.OrdinalIgnoreCase);
    }
}
