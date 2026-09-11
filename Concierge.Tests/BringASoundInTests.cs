using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;

namespace Concierge.Tests;

/// <summary>
/// A track brought in from a link — Antra's last piece, and the one design tool
/// that asks first.
///
/// Everything else on the canvas acts without asking, because going back is free
/// and asking is what makes the surface unusable for the people it is for. That
/// argument holds exactly as far as the edge of the device. This one makes a
/// request to an address a model may have written, and no amount of picking an
/// earlier picture un-makes it: the request happened, and whoever is at the other
/// end knows it.
///
/// So the tests here are mostly about refusing: what it will not fetch, what it
/// will not accept, and that it does nothing at all until somebody says yes.
/// </summary>
public sealed class BringASoundInTests
{
    private static DesignWorkbench Open(IWebAccess? web, IToolApprovalService? approval)
    {
        var bench = new DesignWorkbench();
        bench.Attach(new DesignSession());
        bench.Web = web;
        bench.Approval = approval;
        return bench;
    }

    private static IAgentTool? Tool(DesignWorkbench bench)
        => new DesignToolSource(bench).Tools.FirstOrDefault(t => t.Name == "design_bring_in_sound");

    private static JsonObject At(string url) => new() { ["url"] = url };

    // ── Whether it is offered ─────────────────────────────────────────────

    /// <summary>
    /// A head that cannot ask must not be able to do it. Absent, rather than
    /// present and silently reaching the internet.
    /// </summary>
    [Fact]
    public void Without_a_way_to_ask_it_is_not_offered()
        => Assert.Null(Tool(Open(new Willing(), approval: null)));

    [Fact]
    public void Without_a_way_to_reach_the_web_it_is_not_offered()
        => Assert.Null(Tool(Open(web: null, approval: new Allows())));

    [Fact]
    public void With_both_it_is_offered()
        => Assert.NotNull(Tool(Open(new Willing(), new Allows())));

    // ── Asking first ──────────────────────────────────────────────────────

    /// <summary>
    /// Nothing is fetched before the answer comes back. The order matters more
    /// than it sounds: a tool that fetches and then asks has already done the
    /// irreversible part.
    /// </summary>
    [Fact]
    public async Task Nothing_is_fetched_until_somebody_says_yes()
    {
        var web = new Willing();
        var result = await Tool(Open(web, new Refuses()))!.InvokeAsync(At("https://example.com/a.mp3"));

        Assert.False(result.Success);
        Assert.Equal(0, web.Asked);
    }

    /// <summary>
    /// "Fetch a sound" is not something anybody can make a decision about. The
    /// address is the decision, so the address is what the card carries.
    /// </summary>
    [Fact]
    public async Task The_address_is_what_is_put_on_the_card()
    {
        var approval = new Allows();
        await Tool(Open(new Willing(), approval))!.InvokeAsync(At("https://example.com/tracks/one.mp3"));

        Assert.NotNull(approval.Seen);
        Assert.Equal("https://example.com/tracks/one.mp3", approval.Seen!.Summary);
        Assert.Contains("internet", approval.Seen.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refusal_leaves_the_canvas_exactly_as_it_was()
    {
        var bench = Open(new Willing(), new Refuses());
        var before = bench.Session!.Current;

        await Tool(bench)!.InvokeAsync(At("https://example.com/a.mp3"));

        Assert.Same(before, bench.Session.Current);
    }

    // ── What comes back ───────────────────────────────────────────────────

    [Fact]
    public async Task What_is_fetched_becomes_a_track_on_the_canvas()
    {
        var bench = Open(new Willing(), new Allows());

        var result = await Tool(bench)!.InvokeAsync(At("https://example.com/one.mp3"));

        Assert.True(result.Success, result.FailureMessage);

        var track = bench.Session!.Current.ChildrenOf(bench.Session.Current.RootId).Single();
        Assert.Equal(DesignNodeKind.Sound, track.Kind);
    }

    /// <summary>
    /// Carried, not referenced. A design pointing at somebody else's server is a
    /// design that stops working when they tidy up — the same rule the paperclip
    /// follows for a picture.
    /// </summary>
    [Fact]
    public async Task The_track_travels_with_the_design_rather_than_pointing_at_a_server()
    {
        var bench = Open(new Willing(), new Allows());
        await Tool(bench)!.InvokeAsync(At("https://example.com/one.mp3"));

        var track = bench.Session!.Current.ChildrenOf(bench.Session.Current.RootId).Single();

        Assert.StartsWith("data:audio/mpeg;base64,", track.Props["src"], StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", track.Props["src"], StringComparison.Ordinal);
    }

    /// <summary>
    /// A track labelled with a whole URL is a track nobody can read on a canvas.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/tracks/feeling-good.mp3", "feeling good")]
    [InlineData("https://example.com/a/Wild_Is_The_Wind.mp3", "Wild Is The Wind")]
    [InlineData("https://example.com/", "A sound")]
    public async Task A_name_comes_out_of_the_address_when_nobody_gives_one(string url, string expected)
    {
        var bench = Open(new Willing(), new Allows());
        await Tool(bench)!.InvokeAsync(At(url));

        Assert.Equal(expected, bench.Session!.Current.ChildrenOf(bench.Session.Current.RootId).Single().Text);
    }

    [Fact]
    public async Task A_name_that_is_given_is_the_name_that_is_used()
    {
        var bench = Open(new Willing(), new Allows());

        await Tool(bench)!.InvokeAsync(new JsonObject
        {
            ["url"] = "https://example.com/one.mp3",
            ["name"] = "The theme",
        });

        Assert.Equal("The theme", bench.Session!.Current.ChildrenOf(bench.Session.Current.RootId).Single().Text);
    }

    // ── When it cannot ────────────────────────────────────────────────────

    [Fact]
    public async Task An_address_that_could_not_be_fetched_says_why_and_adds_nothing()
    {
        var bench = Open(new Refusing("That is text/html, not audio."), new Allows());

        var result = await Tool(bench)!.InvokeAsync(At("https://example.com/a.html"));

        Assert.False(result.Success);
        Assert.Equal("That is text/html, not audio.", result.FailureMessage);
        Assert.Empty(bench.Session!.Current.ChildrenOf(bench.Session.Current.RootId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Nothing_to_fetch_is_said_rather_than_attempted(string url)
    {
        var web = new Willing();
        var result = await Tool(Open(web, new Allows()))!.InvokeAsync(At(url));

        Assert.False(result.Success);
        Assert.Equal(0, web.Asked);
    }

    /// <summary>
    /// It asks for audio and only audio. The guard that matters is in IWebAccess —
    /// this holds that the tool actually uses it, because a caller that asked for
    /// anything would make the guard pointless.
    /// </summary>
    [Fact]
    public async Task It_asks_for_audio_and_sets_a_limit()
    {
        var web = new Willing();
        await Tool(Open(web, new Allows()))!.InvokeAsync(At("https://example.com/a.mp3"));

        Assert.Equal("audio/", web.WantedType);
        Assert.True(web.Cap is > 0 and <= 32 * 1024 * 1024, $"cap was {web.Cap}");
    }

    // ── Stand-ins ─────────────────────────────────────────────────────────

    private sealed class Willing : IWebAccess
    {
        public int Asked { get; private set; }

        public string? WantedType { get; private set; }

        public int Cap { get; private set; }

        public Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This tool fetches bytes, not text.");

        public Task<WebBytesResult> FetchBytesAsync(
            string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default)
        {
            Asked++;
            WantedType = expectedType;
            Cap = maxBytes;

            return Task.FromResult(new WebBytesResult(true, url, "audio/mpeg", [1, 2, 3, 4], null));
        }
    }

    private sealed class Refusing(string why) : IWebAccess
    {
        public Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WebBytesResult> FetchBytesAsync(
            string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebBytesResult(false, url, string.Empty, [], why));
    }

    private sealed class Allows : IToolApprovalService
    {
        public ToolApprovalRequest? Seen { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Seen = request;
            return ValueTask.FromResult(ToolApprovalDecision.Allowed);
        }
    }

    private sealed class Refuses : IToolApprovalService
    {
        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ToolApprovalDecision.Denied);
    }
}
