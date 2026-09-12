using Concierge.Ai;

namespace Concierge.Tests;

/// <summary>
/// Real speech, through the real model, into real words.
///
/// Everything else about the microphone is checked with a stub that returns whatever it was
/// told to. That proves the wiring and proves nothing about whether this device can hear —
/// and "the pipeline is connected" is exactly the kind of claim this repository has spent its
/// history catching.
///
/// So: Windows' own speech synthesiser says a known sentence into a WAV, and the whisper model
/// that was downloaded onto this machine is asked what it was. Nothing is stubbed.
///
/// Gated behind <c>CONCIERGE_HEAR</c>, because it needs a 78 MB model on disk and takes real
/// seconds. The result is in the commit message.
/// </summary>
public sealed class ItActuallyHearsTests
{
    [Fact]
    public async Task The_model_on_this_machine_writes_down_what_was_said()
    {
        if (Environment.GetEnvironmentVariable("CONCIERGE_HEAR") != "1")
        {
            return;
        }

        var spoken = Environment.GetEnvironmentVariable("CONCIERGE_HEAR_FILE");

        Assert.False(string.IsNullOrWhiteSpace(spoken), "CONCIERGE_HEAR_FILE was not set.");
        Assert.True(File.Exists(spoken), $"no recording at {spoken}");

        var models = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Concierge",
            "models");

        var files = LocalVoiceFiles.Look(models);

        Assert.NotNull(files.Listener);

        var runtime = new CircleAiVoiceRuntime(new LocalVoiceOptions { ModelsDirectory = models });

        Assert.True(runtime.SupportsTranscription, runtime.StatusMessage);
        Assert.True(runtime.IsReady, runtime.StatusMessage);

        await using var audio = File.OpenRead(spoken!);

        var heard = await runtime.TranscribeAsync(audio, Path.GetFileName(spoken)!);

        Assert.True(
            !string.IsNullOrWhiteSpace(heard.Text),
            heard.Why ?? "no words and no reason given");

        // The words, not the punctuation or the capitals — whisper-tiny is the smallest model
        // in the catalogue and this is a test of whether it heard, not of how it writes.
        var words = new string(heard.Text.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());

        Assert.Contains("wall", words, StringComparison.Ordinal);
        Assert.Contains("room", words, StringComparison.Ordinal);
    }
}
