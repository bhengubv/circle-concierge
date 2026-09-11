using System.Text.Json.Nodes;
using Concierge.Shared.Design;
using Concierge.Shared.Media;

namespace Concierge.Tests;

/// <summary>
/// Writing out what is said in a recording, as a tool.
///
/// The thing this has to get right is the absence. Nothing on this machine can hear until
/// somebody puts a whisper model in the folder or sets a cloud key, and a model told it can
/// transcribe will try to — so with nothing able to hear, the tool is not there at all.
/// </summary>
public sealed class TranscribeToolTests
{
    /// <summary>A voice runtime that says what it can do and hands back what it was given.</summary>
    private sealed class Stand(string id, bool hears, string says = "") : IVoiceRuntime
    {
        public int Asked { get; private set; }

        public string Id => id;
        public string EngineLabel => id;
        public bool IsReady => hears;
        public string StatusMessage => "Standing in";
        public bool SupportsTranscription => hears;
        public bool SupportsSynthesis => false;

        public Task<TranscriptionResult> TranscribeAsync(
            Stream audio, string fileName, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(new TranscriptionResult(id, says, "en", TimeSpan.FromSeconds(1)));
        }

        public Task<SpeechResult> SynthesizeAsync(
            string text, string voice = "alloy", CancellationToken cancellationToken = default)
            => Task.FromResult(new SpeechResult(id, "audio/wav", []));
    }

    private static string AFile(string name = "talk.m4a")
    {
        var folder = Path.Combine(Path.GetTempPath(), "concierge-transcribe", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        return path;
    }

    private static Task<Concierge.Shared.Tools.AgentToolResult> AskAsync(
        TranscribeToolSource source, JsonObject arguments)
        => source.Tools.Single().InvokeAsync(arguments);

    // ── Whether it is there at all ────────────────────────────────────────

    [Fact]
    public void With_nothing_that_can_hear_there_is_no_tool()
        => Assert.Empty(new TranscribeToolSource([new Stand("circleai-voice", hears: false)]).Tools);

    [Fact]
    public void With_nothing_registered_at_all_there_is_no_tool()
        => Assert.Empty(new TranscribeToolSource([]).Tools);

    [Fact]
    public void With_something_that_can_hear_it_is_offered_by_name()
    {
        var tools = new TranscribeToolSource([new Stand("openai-voice", hears: true)]).Tools;

        Assert.Equal("media_transcribe", Assert.Single(tools).Name);
    }

    /// <summary>
    /// Reading a recording is not an act, so it does not ask — the rule `read_file` and the
    /// rest of the media inspection tools follow.
    /// </summary>
    [Fact]
    public void It_only_reads_so_it_does_not_ask()
        => Assert.True(new TranscribeToolSource([new Stand("openai-voice", hears: true)])
            .Tools.Single().IsReadOnly);

    /// <summary>
    /// A recording is about as personal as a file gets. With both able to hear, the one on
    /// this machine wins — sending it somewhere else should never be the quiet default.
    /// </summary>
    [Fact]
    public void Given_a_choice_it_uses_the_one_on_this_device()
    {
        var source = new TranscribeToolSource(
            [new Stand("openai-voice", hears: true), new Stand("circleai-voice", hears: true)]);

        Assert.Equal("circleai-voice", source.Ears!.Id);
    }

    [Fact]
    public void And_a_cloud_one_is_used_when_it_is_all_there_is()
    {
        var source = new TranscribeToolSource(
            [new Stand("circleai-voice", hears: false), new Stand("openai-voice", hears: true)]);

        Assert.Equal("openai-voice", source.Ears!.Id);
    }

    // ── What it does ──────────────────────────────────────────────────────

    [Fact]
    public async Task It_hands_back_what_was_said()
    {
        var ears = new Stand("circleai-voice", hears: true, says: "The second shot is too long.");

        var result = await AskAsync(new TranscribeToolSource([ears]), new JsonObject { ["path"] = AFile() });

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal("The second shot is too long.", result.Output);
        Assert.Equal(1, ears.Asked);
    }

    /// <summary>
    /// Silence is an answer, not a failure. Reported as an error it sends somebody looking
    /// for a broken tool instead of a muted microphone.
    /// </summary>
    [Fact]
    public async Task Nothing_said_is_an_answer_rather_than_a_failure()
    {
        var result = await AskAsync(
            new TranscribeToolSource([new Stand("circleai-voice", hears: true)]),
            new JsonObject { ["path"] = AFile("quiet.m4a") });

        Assert.True(result.Success);
        Assert.Contains("quiet.m4a", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_is_not_there_says_so_rather_than_being_opened()
    {
        var ears = new Stand("circleai-voice", hears: true);

        var result = await AskAsync(
            new TranscribeToolSource([ears]),
            new JsonObject { ["path"] = Path.Combine(Path.GetTempPath(), "no-such-recording.m4a") });

        Assert.False(result.Success);
        Assert.Equal(0, ears.Asked);
    }

    [Fact]
    public async Task With_no_path_it_asks_for_one()
    {
        var result = await AskAsync(
            new TranscribeToolSource([new Stand("circleai-voice", hears: true)]), []);

        Assert.False(result.Success);
        Assert.Contains("path", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A model writing a number where a path belongs is a slip, not a crash: reading it with
    /// GetValue&lt;string&gt;() throws, which is the root defect the design tools were fixed
    /// for once already.
    /// </summary>
    [Fact]
    public async Task A_path_that_is_not_even_text_is_refused_rather_than_thrown_on()
    {
        var result = await AskAsync(
            new TranscribeToolSource([new Stand("circleai-voice", hears: true)]),
            new JsonObject { ["path"] = 12 });

        Assert.False(result.Success);
    }
}
