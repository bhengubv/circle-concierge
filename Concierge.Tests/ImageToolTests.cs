using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Design;
using Concierge.Shared.Media;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;

namespace Concierge.Tests;

/// <summary>
/// Making a picture, from the harness and onto a design.
///
/// The seam for this has existed for months with two providers behind it, and the only thing
/// that could reach it was the composer: a person typing "draw a fox" got a picture, and a
/// model asked to illustrate a page could not. The capability was there and unreachable —
/// this repository's signature defect again, and the one that left open-design's "image
/// generation" line with no answer.
///
/// **Whoever makes the picture is somebody else's problem.** These check the wiring: that it
/// is absent while nothing is ready, that it asks with the description on the card, and that
/// what lands on a design travels with it rather than pointing at an address that stops
/// working in an hour.
/// </summary>
public sealed class ImageToolTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "concierge-pictures", Guid.NewGuid().ToString("n"));

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

    /// <summary>A maker that answers with bytes, or with an address, or not at all.</summary>
    private sealed class Maker(bool ready, string? url = null, byte[]? bytes = null, string id = "openai-images")
        : IImageRuntime
    {
        public string? Asked { get; private set; }

        public string Id => id;
        public string EngineLabel => "A Maker";
        public bool IsReady => ready;
        public string StatusMessage => ready ? "Ready" : "Needs a key";

        public Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
            ImageGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Asked = request.Prompt;

            return Task.FromResult<IReadOnlyList<ImageArtifact>>(
                url is null && bytes is null
                    ? []
                    : [new ImageArtifact(id, request.Prompt, "image/png", url, bytes, DateTimeOffset.UtcNow)]);
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

    private sealed class Fetching(byte[]? bytes) : IWebAccess
    {
        public Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(new WebFetchResult(true, url, null, string.Empty, 0, false));

        public Task<WebBytesResult> FetchBytesAsync(
            string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default)
            => Task.FromResult(bytes is null
                ? new WebBytesResult(false, url, string.Empty, [], "It could not be fetched.")
                : new WebBytesResult(true, url, "image/png", bytes, null));
    }

    private static readonly byte[] APicture = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    // ── Whether it is offered at all ──────────────────────────────────────

    [Fact]
    public void With_nothing_ready_to_make_a_picture_nothing_is_offered()
        => Assert.Empty(new ImageToolSource(
            [new Maker(ready: false)], new Answers(ToolApprovalDecision.Allowed)).Tools);

    /// <summary>
    /// The null runtime exists so the images page always has something to render. It is not a
    /// maker, and offering a tool backed by it would be a tool that always fails.
    /// </summary>
    [Fact]
    public void The_stand_in_runtime_is_not_mistaken_for_one_that_can()
        => Assert.Empty(new ImageToolSource(
            [new NullImageRuntime()], new Answers(ToolApprovalDecision.Allowed)).Tools);

    [Fact]
    public void With_no_way_to_ask_nothing_is_offered_either()
        => Assert.Empty(new ImageToolSource([new Maker(ready: true, bytes: APicture)]).Tools);

    [Fact]
    public void With_one_ready_it_is_offered_by_name()
        => Assert.Equal(
            "make_picture",
            Assert.Single(new ImageToolSource(
                [new Maker(ready: true, bytes: APicture)],
                new Answers(ToolApprovalDecision.Allowed)).Tools).Name);

    /// <summary>
    /// A prompt is somebody's idea. Sending it to a company they have never heard of should
    /// not be what happens when they say nothing.
    /// </summary>
    [Fact]
    public void Given_a_choice_the_one_on_this_device_makes_it()
    {
        var source = new ImageToolSource(
            [new Maker(ready: true, bytes: APicture), new Maker(ready: true, bytes: APicture, id: "circleai-images")],
            new Answers(ToolApprovalDecision.Allowed));

        Assert.Equal("circleai-images", source.Maker!.Id);
    }

    // ── Making one to a file ──────────────────────────────────────────────

    private IAgentTool Tool(IImageRuntime maker, IToolApprovalService approval, IWebAccess? web = null)
        => new ImageToolSource([maker], approval, web, _folder).Tools.Single();

    [Fact]
    public async Task It_writes_the_picture_where_somebody_can_find_it()
    {
        var maker = new Maker(ready: true, bytes: APicture);

        var result = await Tool(maker, new Answers(ToolApprovalDecision.Allowed))
            .InvokeAsync(new JsonObject { ["of"] = "a fox in the snow" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal("a fox in the snow", maker.Asked);

        // Named from the words, so a folder of pictures can be read rather than searched.
        var path = Path.Combine(_folder, "a-fox-in-the-snow.png");

        Assert.True(File.Exists(path));
        Assert.Equal(APicture, await File.ReadAllBytesAsync(path));
    }

    /// <summary>
    /// "Make a picture" is not something anybody can decide about. The words being sent to a
    /// company are.
    /// </summary>
    [Fact]
    public async Task It_asks_with_the_description_on_the_card()
    {
        var approver = new Answers(ToolApprovalDecision.Denied);
        var maker = new Maker(ready: true, bytes: APicture);

        var result = await Tool(maker, approver).InvokeAsync(new JsonObject { ["of"] = "a fox in the snow" });

        Assert.False(result.Success);
        Assert.Equal("a fox in the snow", approver.Asked!.Summary);
        Assert.Null(maker.Asked);
        Assert.False(Directory.Exists(_folder));
    }

    /// <summary>
    /// Some providers host the picture rather than handing it over, and the address stops
    /// working. It is fetched through the same guard every other fetch uses.
    /// </summary>
    [Fact]
    public async Task A_picture_the_provider_only_hosts_is_fetched_and_saved()
    {
        var result = await Tool(
                new Maker(ready: true, url: "https://example.test/fox.png"),
                new Answers(ToolApprovalDecision.Allowed),
                new Fetching(APicture))
            .InvokeAsync(new JsonObject { ["of"] = "a fox" });

        Assert.True(result.Success, result.FailureMessage);
        Assert.True(File.Exists(Path.Combine(_folder, "a-fox.png")));
    }

    /// <summary>
    /// On a head that cannot fetch, the address is a real answer and better than a failure:
    /// somebody can still open it.
    /// </summary>
    [Fact]
    public async Task On_a_head_that_cannot_fetch_the_address_comes_back_instead()
    {
        var result = await Tool(
                new Maker(ready: true, url: "https://example.test/fox.png"),
                new Answers(ToolApprovalDecision.Allowed))
            .InvokeAsync(new JsonObject { ["of"] = "a fox" });

        Assert.True(result.Success);
        Assert.Contains("https://example.test/fox.png", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_coming_back_is_a_failure_rather_than_an_empty_file()
    {
        var result = await Tool(new Maker(ready: true), new Answers(ToolApprovalDecision.Allowed))
            .InvokeAsync(new JsonObject { ["of"] = "a fox" });

        Assert.False(result.Success);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task With_nothing_described_it_asks_and_sends_nothing()
    {
        var approver = new Answers(ToolApprovalDecision.Allowed);

        var result = await Tool(new Maker(ready: true, bytes: APicture), approver).InvokeAsync(new JsonObject());

        Assert.False(result.Success);
        Assert.Null(approver.Asked);
    }

    [Theory]
    [InlineData("A Fox, in the Snow!", "a-fox-in-the-snow.png")]
    [InlineData("   ", "picture.png")]
    public void The_filename_is_the_words(string words, string expected)
        => Assert.Equal(expected, ImageWords.NameFor(words, "image/png"));

    // ── Onto a design ─────────────────────────────────────────────────────

    private static DesignWorkbench Canvas(IImageRuntime? maker, IToolApprovalService? approval, IWebAccess? web = null)
    {
        var bench = new DesignWorkbench { Pictures = maker, Approval = approval, Web = web };
        bench.Attach(new DesignSession());

        return bench;
    }

    private static IAgentTool? OnCanvas(DesignWorkbench bench)
        => new DesignToolSource(bench).Tools.SingleOrDefault(tool => tool.Name == "design_picture");

    [Fact]
    public void With_nothing_that_can_make_one_the_canvas_is_not_offered_it()
    {
        Assert.Null(OnCanvas(Canvas(null, new Answers(ToolApprovalDecision.Allowed))));
        Assert.Null(OnCanvas(Canvas(new Maker(ready: false), new Answers(ToolApprovalDecision.Allowed))));
        Assert.Null(OnCanvas(Canvas(new Maker(ready: true, bytes: APicture), null)));
    }

    /// <summary>
    /// Carried as a data URI the way the paperclip carries a picture, so the design still
    /// travels rather than pointing at a provider's address that stops working in an hour.
    /// </summary>
    [Fact]
    public async Task A_picture_made_for_a_design_travels_with_it()
    {
        var bench = Canvas(new Maker(ready: true, bytes: APicture), new Answers(ToolApprovalDecision.Allowed));

        var result = await OnCanvas(bench)!.InvokeAsync(new JsonObject { ["of"] = "a fox in the snow" });

        Assert.True(result.Success, result.FailureMessage);

        var picture = Assert.Single(
            bench.Session!.Current.Nodes.Values.Where(node => node.Kind == DesignNodeKind.Image));

        Assert.StartsWith("data:image/png;base64,", picture.Props["src"], StringComparison.Ordinal);
        Assert.Equal("a fox in the snow", picture.Text);
    }

    [Fact]
    public async Task It_can_be_put_on_one_particular_slide()
    {
        var bench = Canvas(new Maker(ready: true, bytes: APicture), new Answers(ToolApprovalDecision.Allowed));

        var slide = DesignNode.New(DesignNodeKind.Frame, null, ("text", "A slide"));
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Deck).Add(slide), "Added a slide");

        await OnCanvas(bench)!.InvokeAsync(new JsonObject { ["of"] = "a fox", ["on"] = slide.Id });

        Assert.Contains(
            bench.Session.Current.ChildrenOf(slide.Id),
            node => node.Kind == DesignNodeKind.Image);
    }

    /// <summary>
    /// A picture generated onto a frame that is not there costs a request somebody paid for
    /// and shows nobody anything.
    /// </summary>
    [Fact]
    public async Task A_slide_that_is_not_there_is_caught_before_anything_is_sent()
    {
        var maker = new Maker(ready: true, bytes: APicture);
        var approver = new Answers(ToolApprovalDecision.Allowed);
        var bench = Canvas(maker, approver);

        var result = await OnCanvas(bench)!.InvokeAsync(
            new JsonObject { ["of"] = "a fox", ["on"] = "nothing" });

        Assert.False(result.Success);
        Assert.Null(maker.Asked);
        Assert.Null(approver.Asked);
    }

    /// <summary>
    /// The second design tool that asks, for the same reason as the first: no amount of
    /// picking an earlier picture un-makes a request somebody else has received and billed.
    /// </summary>
    [Fact]
    public async Task Refused_means_nothing_was_sent_and_nothing_was_added()
    {
        var maker = new Maker(ready: true, bytes: APicture);
        var bench = Canvas(maker, new Answers(ToolApprovalDecision.Denied));

        var result = await OnCanvas(bench)!.InvokeAsync(new JsonObject { ["of"] = "a fox" });

        Assert.False(result.Success);
        Assert.Null(maker.Asked);
        Assert.True(bench.Session!.Current.IsEmpty);
    }

    /// <summary>
    /// An address in a design stops working, so a design would rather have nothing than a
    /// picture that disappears.
    /// </summary>
    [Fact]
    public async Task A_picture_that_cannot_be_fetched_is_not_put_on_the_design_as_an_address()
    {
        var bench = Canvas(
            new Maker(ready: true, url: "https://example.test/fox.png"),
            new Answers(ToolApprovalDecision.Allowed));

        var result = await OnCanvas(bench)!.InvokeAsync(new JsonObject { ["of"] = "a fox" });

        Assert.False(result.Success);
        Assert.True(bench.Session!.Current.IsEmpty);
    }
}
