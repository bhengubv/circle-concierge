using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Media;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Written words, spoken, added to the running order.
///
/// The piece that makes a sound design something a person can finish on their own. A running
/// order could hold music and a track fetched from a link, and the one thing nearly every one
/// of them needs — somebody saying the words — needed a microphone and a willing person.
/// </summary>
public sealed class NarrateToolTests
{
    /// <summary>A voice that speaks, or does not, and remembers what it was asked.</summary>
    private sealed class Stand(bool speaks, int bytes = 64) : IVoiceRuntime
    {
        public string? Words { get; private set; }

        public string Id => "circleai-voice";
        public string EngineLabel => "Standing in";
        public bool IsReady => speaks;
        public string StatusMessage => "Standing in";
        public bool SupportsTranscription => false;
        public bool SupportsSynthesis => speaks;

        public Task<TranscriptionResult> TranscribeAsync(
            Stream audio, string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionResult(Id, string.Empty, null, null));

        public Task<SpeechResult> SynthesizeAsync(
            string text, string voice = "alloy", CancellationToken cancellationToken = default)
        {
            Words = text;
            return Task.FromResult(new SpeechResult(Id, "audio/wav", new byte[bytes]));
        }
    }

    private static DesignWorkbench Open(IVoiceRuntime? speech)
    {
        var bench = new DesignWorkbench { Speech = speech };
        bench.Attach(new DesignSession());
        bench.Session!.Record(bench.Session.Current.As(DesignMedium.Sound), "Made it");

        return bench;
    }

    private static IAgentTool? Narrate(DesignWorkbench bench)
        => new DesignToolSource(bench).Tools.SingleOrDefault(tool => tool.Name == "design_narrate");

    // ── Whether it is there ───────────────────────────────────────────────

    [Fact]
    public void With_nothing_that_can_speak_it_is_not_offered()
        => Assert.Null(Narrate(Open(null)));

    [Fact]
    public void Nor_when_what_is_there_can_only_listen()
        => Assert.Null(Narrate(Open(new Stand(speaks: false))));

    [Fact]
    public void With_something_that_can_speak_it_is_offered()
        => Assert.NotNull(Narrate(Open(new Stand(speaks: true))));

    /// <summary>
    /// The one design tool that asks is the one that leaves the device. This does not leave
    /// it, writes no file anybody else can see, and going back is free — so it does not ask,
    /// like everything else on this canvas.
    /// </summary>
    [Fact]
    public async Task It_does_not_ask_because_nothing_about_it_leaves_the_device()
    {
        // No approval service on the bench at all. The tool that fetches a track from a link
        // is not even offered without one; this one works, because nothing about it leaves.
        var bench = Open(new Stand(speaks: true));

        Assert.Null(bench.Approval);
        Assert.True((await Narrate(bench)!.InvokeAsync(new JsonObject { ["words"] = "Hello" })).Success);
    }

    // ── What it does ──────────────────────────────────────────────────────

    [Fact]
    public async Task It_adds_a_track_carrying_the_audio_inside_the_design()
    {
        var voice = new Stand(speaks: true);
        var bench = Open(voice);

        var result = await Narrate(bench)!.InvokeAsync(
            new JsonObject { ["words"] = "Here is where the story turns." });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal("Here is where the story turns.", voice.Words);

        var track = Assert.Single(
            bench.Session!.Current.Nodes.Values.Where(node => node.Kind == DesignNodeKind.Sound));

        // Carried as a data URI the way a picture and a fetched track already are, so the
        // design still travels rather than pointing at a file on this machine.
        Assert.StartsWith("data:audio/wav;base64,", track.Props["src"], StringComparison.Ordinal);
        Assert.Equal("Here is where the story turns.", track.Props["words"]);
    }

    [Fact]
    public async Task The_track_is_named_from_the_words_when_nobody_names_it()
    {
        var bench = Open(new Stand(speaks: true));

        await Narrate(bench)!.InvokeAsync(new JsonObject { ["words"] = "Good evening." });

        Assert.Equal(
            "Good evening.",
            bench.Session!.Current.Nodes.Values.Single(node => node.Kind == DesignNodeKind.Sound).Text);
    }

    /// <summary>
    /// A whole paragraph as a caption is a running order nobody can read.
    /// </summary>
    [Fact]
    public async Task A_long_line_is_cut_short_for_the_caption_and_kept_in_full_in_the_words()
    {
        var bench = Open(new Stand(speaks: true));
        var saying = string.Join(' ', Enumerable.Repeat("something worth saying", 20));

        await Narrate(bench)!.InvokeAsync(new JsonObject { ["words"] = saying });

        var track = bench.Session!.Current.Nodes.Values.Single(node => node.Kind == DesignNodeKind.Sound);

        Assert.True(track.Text.Length <= 41, track.Text);
        Assert.Equal(saying, track.Props["words"]);
    }

    [Fact]
    public async Task A_name_that_was_given_is_the_one_used()
    {
        var bench = Open(new Stand(speaks: true));

        await Narrate(bench)!.InvokeAsync(
            new JsonObject { ["words"] = "Good evening.", ["name"] = "Opening line" });

        Assert.Equal(
            "Opening line",
            bench.Session!.Current.Nodes.Values.Single(node => node.Kind == DesignNodeKind.Sound).Text);
    }

    [Fact]
    public async Task With_no_words_it_asks_for_some_and_adds_nothing()
    {
        var bench = Open(new Stand(speaks: true));

        var result = await Narrate(bench)!.InvokeAsync(new JsonObject());

        Assert.False(result.Success);
        Assert.DoesNotContain(bench.Session!.Current.Nodes.Values, node => node.Kind == DesignNodeKind.Sound);
    }

    /// <summary>
    /// A silent track on the canvas looks exactly like one that worked, which is this
    /// repository's signature defect: a surface asserting something untrue.
    /// </summary>
    [Fact]
    public async Task Nothing_coming_back_is_a_failure_rather_than_a_silent_track()
    {
        var bench = Open(new Stand(speaks: true, bytes: 0));

        var result = await Narrate(bench)!.InvokeAsync(new JsonObject { ["words"] = "Hello" });

        Assert.False(result.Success);
        Assert.DoesNotContain(bench.Session!.Current.Nodes.Values, node => node.Kind == DesignNodeKind.Sound);
    }

    /// <summary>
    /// Every state is kept whole, so a narration somebody did not want costs one press.
    /// </summary>
    [Fact]
    public async Task Adding_one_can_be_gone_back_from()
    {
        var bench = Open(new Stand(speaks: true));

        await Narrate(bench)!.InvokeAsync(new JsonObject { ["words"] = "Good evening." });

        Assert.True(bench.Session!.CanGoBack);
    }
}
