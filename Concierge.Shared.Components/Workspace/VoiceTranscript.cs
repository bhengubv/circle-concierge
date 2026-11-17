using System.Net;
using System.Net.Http.Json;
using Concierge.Shared.Media;

namespace Concierge.Shared.Components.Workspace;

/// <summary>One transcription path for the composer and the app-wide B! control.</summary>
public static class VoiceTranscript
{
    public sealed record Result(string? Text, string? Problem);

    public static async Task<Result> TranscribeAsync(string base64, IServiceProvider services, Uri baseUri)
    {
        byte[] audio;
        try { audio = Convert.FromBase64String(base64); }
        catch (FormatException) { return new(null, "That recording could not be read."); }

        var voices = services.GetService(typeof(IEnumerable<IVoiceRuntime>)) as IEnumerable<IVoiceRuntime> ?? [];
        var ears = VoiceChoice.Ears(voices.Where(voice => voice.IsReady));
        if (ears is not null)
        {
            try
            {
                using var sound = new MemoryStream(audio, writable: false);
                var said = await ears.TranscribeAsync(sound, "capture.webm");
                return string.IsNullOrWhiteSpace(said.Text)
                    ? new(null, string.IsNullOrWhiteSpace(said.Why) ? "Nothing was said in that." : said.Why)
                    : new(said.Text, null);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            { return new(null, $"That could not be written down: {failure.Message}"); }
        }

        try
        {
            using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(45) };
            using var response = await http.PostAsJsonAsync("api/voice/transcribe",
                new { audioBase64 = base64, fileName = "capture.webm" });
            if (!response.IsSuccessStatusCode)
                return new(null, response.StatusCode == HttpStatusCode.NotFound
                    ? "Nothing on this device can listen yet."
                    : $"That could not be written down ({(int)response.StatusCode}).");
            var transcript = await response.Content.ReadFromJsonAsync<TranscriptResponse>();
            return string.IsNullOrWhiteSpace(transcript?.Text)
                ? new(null, "Nothing was said in that.") : new(transcript.Text, null);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        { return new(null, "Nothing on this device can listen yet."); }
    }

    private sealed record TranscriptResponse(string? Text);
}
