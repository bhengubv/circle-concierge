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

public sealed record TranscriptionResult(
    string RuntimeId,
    string Text,
    string? Language,
    TimeSpan? Duration);

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
