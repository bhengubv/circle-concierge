using Concierge.Ai;
using Concierge.Shared.Design;
using Concierge.Shared.Media;
using Concierge.Shared.Devices;

namespace Concierge.Platforms.Android;

/// <summary>Speech through Circle AI's authorised Binder audio link.</summary>
public sealed class CircleAiServiceVoiceRuntime(
    AndroidCircleAiSharedService service,
    ICircleAiServiceAudioLink audio) : IVoiceRuntime
{
    public string Id => "circleai-service-voice";
    public string EngineLabel => "Circle AI service on this device";
    public bool IsReady => service.VoiceAuthorized;
    public string StatusMessage => IsReady
        ? "Circle AI voice link is connected; speech model readiness remains owned and reported by Circle AI."
        : "Connect Concierge to Circle AI with Voice access to use the service speech models.";
    public bool SupportsTranscription => true;
    public bool SupportsSynthesis => true;

    public async Task<TranscriptionResult> TranscribeAsync(Stream recording, string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!IsReady) throw new InvalidOperationException(StatusMessage);
        var root = FileSystem.CacheDirectory;
        var input = Path.Combine(root, "circleai-voice-" + Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
        var output = Path.Combine(root, "circleai-voice-" + Guid.NewGuid().ToString("N") + ".pcm");
        try
        {
            await using (var target = File.Create(input)) await recording.CopyToAsync(target, cancellationToken);
            var encoded = await Encoders.Runner.RunAsync([
                "-hide_banner", "-loglevel", "error", "-y", "-i", input,
                "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "-acodec", "pcm_s16le", output
            ], cancellationToken);
            if (!encoded.Ok || !File.Exists(output))
                throw new InvalidOperationException("The recording could not be converted for Circle AI: " + encoded.Said);
            var pcm = await File.ReadAllBytesAsync(output, cancellationToken);
            var result = await audio.TranscribePcmAsync(pcm, System.Globalization.CultureInfo.CurrentUICulture.Name, cancellationToken);
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Circle AI could not transcribe this recording.");
            return new(Id, result.Text ?? "", null, TimeSpan.FromSeconds(pcm.Length / 32_000d));
        }
        finally
        {
            TryDelete(input); TryDelete(output);
        }
    }

    public async Task<SpeechResult> SynthesizeAsync(string text, string voice = "alloy",
        CancellationToken cancellationToken = default)
    {
        if (!IsReady) throw new InvalidOperationException(StatusMessage);
        var result = await audio.SpeakAsync(text, System.Globalization.CultureInfo.CurrentUICulture.Name, cancellationToken);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "Circle AI could not speak this reply.");
        return new(Id, "audio/wav", Wav(result.Audio));
    }

    private static byte[] Wav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        using var stream = new MemoryStream(wav, writable: true);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16_000); writer.Write(32_000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
        return wav;
    }

    private static void TryDelete(string path)
    { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
