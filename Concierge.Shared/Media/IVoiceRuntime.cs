namespace Concierge.Shared.Media;

/// <summary>
/// Host-neutral voice surface — transcription (speech-to-text) and synthesis (text-to-speech).
/// Adapters wrap OpenAI Whisper / TTS, Google Cloud Speech, CircleAI's SenseVoice once that
/// integration lands. Same pattern as <see cref="IImageRuntime"/>: register one per provider,
/// UI walks the collection.
/// </summary>
public interface IVoiceRuntime
{
    string Id { get; }
    string EngineLabel { get; }
    bool IsReady { get; }
    string StatusMessage { get; }

    /// <summary>
    /// <c>true</c> if the runtime can transcribe audio. Implementations can be STT-only,
    /// TTS-only, or both.
    /// </summary>
    bool SupportsTranscription { get; }

    /// <summary>
    /// <c>true</c> if the runtime can synthesize speech.
    /// </summary>
    bool SupportsSynthesis { get; }

    Task<TranscriptionResult> TranscribeAsync(
        Stream audio,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<SpeechResult> SynthesizeAsync(
        string text,
        string voice = "alloy",
        CancellationToken cancellationToken = default);
}

/// <param name="Why">
/// Why there are no words, when there are none.
///
/// **Four different things used to come back as an empty string**: nothing was said, there is
/// no encoder to convert the audio, the model would not open, and the transcription threw. A
/// caller could not tell a quiet room from a broken install, so the microphone reported
/// "Nothing was said in that" for every one of them — which is the defect this repository has
/// spent its history removing, four times over in one method.
///
/// Null when there are words, or when silence really was silence.
/// </param>
public sealed record TranscriptionResult(
    string RuntimeId,
    string Text,
    string? Language,
    TimeSpan? Duration,
    string? Why = null);

public sealed record SpeechResult(
    string RuntimeId,
    string MimeType,
    byte[] Audio);

public sealed class NullVoiceRuntime : IVoiceRuntime
{
    public string Id => "null";
    public string EngineLabel => "No voice runtime";
    public bool IsReady => false;
    public string StatusMessage => "No voice runtime is wired. Configure OpenAI:ApiKey to enable Whisper + TTS.";
    public bool SupportsTranscription => false;
    public bool SupportsSynthesis => false;

    public Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken = default)
        => Task.FromResult(new TranscriptionResult(Id, string.Empty, null, null));

    public Task<SpeechResult> SynthesizeAsync(string text, string voice = "alloy", CancellationToken cancellationToken = default)
        => Task.FromResult(new SpeechResult(Id, "audio/mpeg", Array.Empty<byte>()));
}
