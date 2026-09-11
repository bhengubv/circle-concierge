using System.Buffers.Binary;
using Concierge.Ai;

namespace Concierge.Tests;

/// <summary>
/// Speech on the device, present when its model files are and absent otherwise.
///
/// CircleAI.Voice sat in the package cache with no caller at all — WhisperTranscriber for
/// listening, OnnxTtsEngine for speaking, both complete, both unreachable. This is the
/// wiring, and what it has to get right is the absent case: the model files are large, not
/// ours to redistribute, and not on this machine. A runtime that says it is ready and then
/// cannot do anything is the defect this repository keeps finding.
///
/// So none of these tests need a model. What they check is that the absence is honest, that
/// nothing throws because of it, and that what a player is handed is a file it can open.
/// </summary>
public sealed class LocalVoiceTests : IDisposable
{
    private readonly string _models = Path.Combine(
        Path.GetTempPath(), "concierge-voice-tests", Guid.NewGuid().ToString("n"));

    private string Voice(string name)
    {
        var folder = LocalVoiceFiles.FolderUnder(_models);
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_models))
            {
                Directory.Delete(_models, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ── What is on disk ───────────────────────────────────────────────────

    [Fact]
    public void With_nothing_on_disk_there_is_nothing_to_find()
    {
        var found = LocalVoiceFiles.Look(_models);

        Assert.Null(found.Listener);
        Assert.Null(found.Speaker);
        Assert.False(found.Anything);
    }

    /// <summary>
    /// The sentence names the folder. Somebody who wants speech needs an answer, not a
    /// runtime that is quietly not ready — which is a screen asserting nothing at all.
    /// </summary>
    [Fact]
    public void And_it_says_where_it_looked_and_what_to_put_there()
    {
        var found = LocalVoiceFiles.Look(_models);

        Assert.Contains(found.Folder, found.Why, StringComparison.Ordinal);
        Assert.Contains(".bin", found.Why, StringComparison.Ordinal);
        Assert.Contains(".onnx", found.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whisper_model_is_what_can_listen()
    {
        var path = Voice("ggml-base.bin");
        var found = LocalVoiceFiles.Look(_models);

        Assert.Equal(path, found.Listener);
        Assert.Null(found.Speaker);
    }

    [Fact]
    public void A_voice_model_is_what_can_speak()
    {
        var path = Voice("en_GB-alba-medium.onnx");
        var found = LocalVoiceFiles.Look(_models);

        Assert.Equal(path, found.Speaker);
        Assert.Null(found.Listener);
    }

    /// <summary>
    /// Half of it is a real state and has to read as one. Told only that speech is "not
    /// ready", somebody with a whisper model and no voice cannot tell which half to fix.
    /// </summary>
    [Fact]
    public void One_without_the_other_says_which_half_is_missing()
    {
        Voice("ggml-base.bin");

        Assert.Contains("Nothing here can speak", LocalVoiceFiles.Look(_models).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_is_not_there_is_the_same_answer_as_an_empty_one()
        => Assert.False(LocalVoiceFiles.Look(Path.Combine(_models, "nowhere", "at", "all")).Anything);

    // ── What the runtime says about itself ────────────────────────────────

    [Fact]
    public void With_no_files_the_runtime_is_not_ready_and_offers_nothing()
    {
        var voice = new CircleAiVoiceRuntime(new LocalVoiceOptions { ModelsDirectory = _models });

        Assert.False(voice.IsReady);
        Assert.False(voice.SupportsTranscription);
        Assert.False(voice.SupportsSynthesis);
        Assert.Contains(voice.Files.Folder, voice.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Listening needs two things, not one: the model, and something that can turn an .m4a
    /// into the samples it reads. Claiming it with only the model is a capability that fails
    /// the first time anybody uses it.
    /// </summary>
    [Fact]
    public void Listening_needs_the_encoder_as_well_as_the_model()
    {
        Voice("ggml-base.bin");

        var voice = new CircleAiVoiceRuntime(new LocalVoiceOptions
        {
            ModelsDirectory = _models,
            FfmpegPath = Path.Combine(_models, "no-encoder-here.exe"),
        });

        Assert.False(voice.SupportsTranscription);
        Assert.Contains("ffmpeg", voice.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Speaking_needs_only_the_model()
    {
        Voice("en_GB-alba-medium.onnx");

        var voice = new CircleAiVoiceRuntime(new LocalVoiceOptions
        {
            ModelsDirectory = _models,
            FfmpegPath = Path.Combine(_models, "no-encoder-here.exe"),
        });

        Assert.True(voice.SupportsSynthesis);
        Assert.True(voice.IsReady);
    }

    // ── What it does when it cannot ───────────────────────────────────────

    [Fact]
    public async Task Asked_to_listen_with_nothing_to_listen_with_it_hears_nothing()
    {
        var voice = new CircleAiVoiceRuntime(new LocalVoiceOptions { ModelsDirectory = _models });

        using var audio = new MemoryStream([1, 2, 3]);
        var heard = await voice.TranscribeAsync(audio, "something.m4a");

        Assert.Equal(string.Empty, heard.Text);
        Assert.Equal("circleai-voice", heard.RuntimeId);
    }

    [Fact]
    public async Task Asked_to_speak_with_nothing_to_speak_with_it_says_nothing()
    {
        var voice = new CircleAiVoiceRuntime(new LocalVoiceOptions { ModelsDirectory = _models });

        var said = await voice.SynthesizeAsync("Hello");

        Assert.Empty(said.Audio);
    }

    /// <summary>
    /// A file that is not a model costs the call, not the app. Somebody will put the wrong
    /// file in that folder, and the native library it reaches is not ours.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_a_model_costs_the_sentence_rather_than_the_app()
    {
        Voice("en_GB-alba-medium.onnx"); // Four bytes of nothing.

        var voice = new CircleAiVoiceRuntime(new LocalVoiceOptions { ModelsDirectory = _models });

        Assert.Empty((await voice.SynthesizeAsync("Hello")).Audio);
    }

    // ── What a player is handed ───────────────────────────────────────────

    /// <summary>
    /// OnnxTtsEngine hands back samples with no header. Nothing plays that — not a browser,
    /// not a phone, not the sound medium on the canvas.
    /// </summary>
    [Fact]
    public void Samples_come_back_as_a_file_something_can_open()
    {
        var pcm = new byte[320];
        var wav = WavAudio.Wrap(pcm, 22050, 1, 16);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal("fmt "u8.ToArray(), wav[12..16]);
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
    }

    [Fact]
    public void And_the_header_describes_the_samples_that_follow_it()
    {
        var wav = WavAudio.Wrap(new byte[640], 16000, 2, 16);

        Assert.Equal(16000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22, 2)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)));

        // Block align is four for stereo sixteen-bit, and the byte rate follows from it.
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32, 2)));
        Assert.Equal(64000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28, 4)));

        Assert.Equal(640u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40, 4)));
        Assert.Equal(676u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4, 4)));
    }

    [Fact]
    public void The_samples_themselves_are_not_changed_on_the_way_through()
    {
        var pcm = new byte[] { 9, 8, 7, 6 };

        Assert.Equal(pcm, WavAudio.Wrap(pcm, 16000, 1, 16)[44..]);
    }
}
