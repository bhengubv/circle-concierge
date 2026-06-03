using Concierge.Shared.Media;

namespace Concierge.Web.Hosting;

/// <summary>
/// Minimal API endpoints the chat page calls from the browser. Both endpoints round-trip
/// through the first ready <see cref="IVoiceRuntime"/> registered in DI — the chat client
/// doesn't pick a provider for voice; whatever the host wired wins. Switching providers is
/// a host concern, not a per-call concern.
/// </summary>
public static class VoiceEndpoints
{
    public static IEndpointRouteBuilder MapConciergeVoice(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/voice/transcribe", async (
            TranscribeRequest body,
            IEnumerable<IVoiceRuntime> runtimes,
            CancellationToken cancellationToken) =>
        {
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

            using var stream = new MemoryStream(audio);
            var result = await runtime.TranscribeAsync(stream, body.FileName ?? "capture.webm", cancellationToken).ConfigureAwait(false);
            return Results.Json(new TranscriptResponse(result.Text, result.Language, null));
        });

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

            var result = await runtime.SynthesizeAsync(body.Text, body.Voice ?? "alloy", cancellationToken).ConfigureAwait(false);
            return Results.File(result.Audio, result.MimeType);
        });

        return app;
    }
}

public sealed record TranscribeRequest(string? AudioBase64, string? FileName);
public sealed record TranscriptResponse(string? Text, string? Language, string? Error);
public sealed record SpeakRequest(string Text, string? Voice);
