using Concierge.Shared.Media;
using Microsoft.AspNetCore.Mvc;

namespace Concierge.Web.Hosting;

/// <summary>
/// Minimal API endpoints the chat page calls from the browser. Both endpoints round-trip
/// through the first ready <see cref="IVoiceRuntime"/> registered in DI — the chat client
/// doesn't pick a provider for voice; whatever the host wired wins. Switching providers is
/// a host concern, not a per-call concern.
/// </summary>
public static class VoiceEndpoints
{
    public const int MaxAudioBytes = 4 * 1024 * 1024;
    public const int MaxSpeechCharacters = 4_000;

    public static IEndpointRouteBuilder MapConciergeVoice(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/voice/transcribe", async (
            TranscribeRequest body,
            IEnumerable<IVoiceRuntime> runtimes,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body.AudioBase64))
                return Results.BadRequest(new TranscriptResponse(null, null, "Empty audio payload."));
            if (body.AudioBase64.Length > ((MaxAudioBytes + 2) / 3) * 4)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var runtime = runtimes.FirstOrDefault(r => r.SupportsTranscription && r.IsReady);
            if (runtime is null)
            {
                return Results.Json(new TranscriptResponse(null, null, "No transcription runtime is configured."));
            }

            byte[] audio;
            try
            {
                audio = Convert.FromBase64String(body.AudioBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new TranscriptResponse(null, null, "audioBase64 was not valid base64."));
            }

            if (audio.Length == 0)
            {
                return Results.BadRequest(new TranscriptResponse(null, null, "Empty audio payload."));
            }

            if (audio.Length > MaxAudioBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            using var stream = new MemoryStream(audio);
            var result = await runtime.TranscribeAsync(stream, SafeFileName(body.FileName), cancellationToken).ConfigureAwait(false);
            return Results.Json(new TranscriptResponse(result.Text, result.Language, null));
        }).WithMetadata(new RequestSizeLimitAttribute(6 * 1024 * 1024));

        app.MapPost("/api/voice/speak", async (
            SpeakRequest body,
            IEnumerable<IVoiceRuntime> runtimes,
            CancellationToken cancellationToken) =>
        {
            var runtime = runtimes.FirstOrDefault(r => r.SupportsSynthesis && r.IsReady);
            if (runtime is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(body.Text))
            {
                return Results.BadRequest("text is required");
            }
            if (body.Text.Length > MaxSpeechCharacters)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var result = await runtime.SynthesizeAsync(body.Text, body.Voice ?? "alloy", cancellationToken).ConfigureAwait(false);
            return Results.File(result.Audio, result.MimeType);
        }).WithMetadata(new RequestSizeLimitAttribute(32 * 1024));

        return app;
    }

    public static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "capture.webm";
        var file = name.Replace('\\', '/').Split('/').Last();
        return file.Length is > 0 and <= 128 ? file : "capture.webm";
    }
}

public sealed record TranscribeRequest(string? AudioBase64, string? FileName);
public sealed record TranscriptResponse(string? Text, string? Language, string? Error);
public sealed record SpeakRequest(string Text, string? Voice);
