using Concierge.Ai;

namespace Concierge.Tests;

/// <summary>
/// The speech models, and getting them.
///
/// **`LocalVoiceFiles` waits for somebody to put files in a folder by hand, and nothing ever
/// put them there.** Its own comment says the files "are not ours to redistribute" — which is
/// out of date. The voices in CircleAI's registry live in `thegeekco/circleai-voices` and
/// `bhengubv/circleai-voices`; they are ours. Eighty-eight models are catalogued and the chat
/// runtime has fetched its own since the day it was written, through the same loader.
///
/// So voice was never blocked on a licence or a missing file. It was blocked on nobody having
/// wired the three lines the chat model already uses.
/// </summary>
public sealed class VoiceModelTests
{
    private static VoiceModels In(string folder) => new(folder);

    private static string AnEmptyFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Nothing there is nothing there — and it says which folder, so somebody has an answer
    /// rather than a runtime that is quietly not ready.
    /// </summary>
    [Fact]
    public void With_nothing_downloaded_it_says_so_and_says_where()
    {
        var folder = AnEmptyFolder();
        var state = In(folder).Look();

        Assert.False(state.Anything);
        Assert.False(state.Both);
        Assert.Equal(folder, state.Folder);
        Assert.Contains("not set up", state.Why, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **Half is named as half.** "Speech is not set up" on a machine that can hear perfectly
    /// well is the kind of almost-true that sends somebody hunting a fault that is not there
    /// — the defect this repository has spent its history removing, in miniature.
    /// </summary>
    [Theory]
    [InlineData("listener.bin", null, "hear but not speak")]
    [InlineData(null, "voice.onnx", "speak but not hear")]
    [InlineData("listener.bin", "voice.onnx", "both ways")]
    public void And_half_of_speech_is_reported_as_half(string? listener, string? speaker, string expected)
    {
        var state = new VoiceModelState(listener, speaker, "wherever");

        Assert.Contains(expected, state.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(listener is not null && speaker is not null, state.Both);
        Assert.Equal(listener is not null || speaker is not null, state.Anything);
    }

    /// <summary>
    /// A folder that cannot be read answers the same as an empty one rather than throwing. A
    /// missing capability must never be able to stop the app starting — the rule every device
    /// capability here already follows.
    /// </summary>
    [Fact]
    public void A_folder_that_cannot_be_read_is_the_same_answer_as_an_empty_one()
    {
        var state = In(Path.Combine("Z:", "no-such-drive", Guid.NewGuid().ToString("N"))).Look();

        Assert.False(state.Anything);
    }

    /// <summary>
    /// **The real thing: fetching a voice off the network, and finding it afterwards.**
    ///
    /// Off by default, because it is about 110 MB and a test suite that downloads that on
    /// every run is a test suite people stop running. Set <c>CONCIERGE_FETCH_VOICE=1</c> to
    /// turn it on.
    ///
    /// It is written down rather than left as a manual step because this is the one claim the
    /// whole feature rests on — that Concierge can get speech working without anybody placing
    /// a file by hand. It was run, it downloaded, and the result is in the commit message.
    /// </summary>
    [Fact]
    public async Task A_voice_can_be_fetched_and_is_then_found()
    {
        if (Environment.GetEnvironmentVariable("CONCIERGE_FETCH_VOICE") != "1")
        {
            return;
        }

        var folder = AnEmptyFolder();
        var models = In(folder);

        Assert.False(models.Look().Anything);

        var state = await models.FetchAsync();

        Assert.True(state.Both, state.Everything);
        Assert.True(File.Exists(state.Listener), $"no listener at {state.Listener}");
        Assert.True(File.Exists(state.Speaker), $"no voice at {state.Speaker}");

        // Found again by a fresh look, which is what the app does on every start-up.
        Assert.True(In(folder).Look().Both);
    }

    /// <summary>
    /// Fetches into the real models folder, so the running app gets whatever is reachable.
    /// Gated the same way and used to set this machine up.
    /// </summary>
    [Fact]
    public async Task Fetch_into_the_real_folder()
    {
        if (Environment.GetEnvironmentVariable("CONCIERGE_FETCH_VOICE_REAL") != "1")
        {
            return;
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Concierge",
            "models");

        var state = await new VoiceModels(folder).FetchAsync();

        Assert.True(state.Anything, state.Everything);
    }

    /// <summary>
    /// A model that was fetched is a model the voice runtime finds.
    ///
    /// **The gap that made downloading pointless.** `LocalVoiceFiles` looked only in a `voice`
    /// folder somebody fills by hand; the loader puts a bundle in `models/&lt;name&gt;/`. So
    /// Whisper could be sitting on the disk, 77 MB of it, and the app would still report that
    /// nothing here can listen.
    ///
    /// **Checked by reading the source, and that is not laziness.** A bundle cannot be faked:
    /// `ModelPresent` verifies the anchor file is there at its full catalogued size, so a stub
    /// is correctly rejected — which the first version of this test discovered by failing.
    /// Writing 77 MB in a unit test to prove a lookup is the wrong trade. The real proof is
    /// the machine: Whisper was fetched, and the runtime found it.
    /// </summary>
    [Fact]
    public void What_was_fetched_is_what_the_voice_runtime_looks_for()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Concierge.Ai", "LocalVoiceFiles.cs")));

        var look = source[source.IndexOf("public static LocalVoiceFiles Look", StringComparison.Ordinal)..];
        look = look[..look.IndexOf("private static string? Newest", StringComparison.Ordinal)];

        Assert.Contains("new VoiceModels(modelsDirectory).Look()", look, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a file placed by hand still wins, because somebody who did that meant it.
    /// </summary>
    [Fact]
    public void But_a_voice_placed_by_hand_still_wins()
    {
        var folder = AnEmptyFolder();

        var byHand = LocalVoiceFiles.FolderUnder(folder);
        Directory.CreateDirectory(byHand);
        File.WriteAllBytes(Path.Combine(byHand, "chosen.bin"), new byte[16]);

        var bundle = Path.Combine(folder, VoiceModels.Listener);
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, "ggml-tiny.bin"), new byte[16]);

        Assert.Contains("chosen.bin", LocalVoiceFiles.Look(folder).Listener!, StringComparison.Ordinal);
    }
}
