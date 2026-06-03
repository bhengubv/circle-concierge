using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Concierge.Shared.Media;
using Microsoft.Extensions.Logging;

namespace Concierge.Media.Cloud;

public sealed class OpenAiVoiceOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.openai.com");
    public string? ApiKey { get; init; }
    public string TranscriptionModel { get; init; } = "whisper-1";
    public string SpeechModel { get; init; } = "tts-1";
    public string DefaultVoice { get; init; } = "alloy";
}

/// <summary>
/// <see cref="IVoiceRuntime"/> backed by OpenAI's Whisper (transcription) + TTS (synthesis)
/// endpoints. Both share a single API key + base address; the runtime advertises support
/// for both directions so the chat UI can wire a microphone input and a "read this back to
/// me" output without juggling two providers.
/// </summary>
public sealed class OpenAiVoiceRuntime : IVoiceRuntime
{
    private readonly HttpClient _http;
    private readonly OpenAiVoiceOptions _options;
    private readonly ILogger<OpenAiVoiceRuntime> _logger;

    public OpenAiVoiceRuntime(HttpClient http, OpenAiVoiceOptions options, ILogger<OpenAiVoiceRuntime> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id => "openai-voice";
    public string EngineLabel => $"OpenAI · {_options.TranscriptionModel} + {_options.SpeechModel}";
    public bool IsReady => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsReady ? "Ready" : "OpenAI API key not configured — set OpenAI:ApiKey to enable Whisper + TTS.";
    public bool SupportsTranscription => IsReady;
    public bool SupportsSynthesis => IsReady;

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            return new TranscriptionResult(Id, string.Empty, null, null);
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var form = new MultipartFormDataContent();
        var audioPart = new StreamContent(audio);
        audioPart.Headers.ContentType = MediaTypeHeaderValue.Parse(GuessAudioMime(fileName));
        form.Add(audioPart, "file", fileName);
        form.Add(new StringContent(_options.TranscriptionModel), "model");
        form.Add(new StringContent("json"), "response_format");
        msg.Content = form;

        using var response = await _http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("OpenAI transcription returned {Status}: {Body}", response.StatusCode, error);
            return new TranscriptionResult(Id, $"[transcription error {(int)response.StatusCode}]", null, null);
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;
        var language = doc.RootElement.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString()
            : null;
        return new TranscriptionResult(Id, text, language, null);
    }

    public async Task<SpeechResult> SynthesizeAsync(string text, string voice = "alloy", CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            return new SpeechResult(Id, "audio/mpeg", Array.Empty<byte>());
        }

        var resolvedVoice = string.IsNullOrWhiteSpace(voice) ? _options.DefaultVoice : voice;
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/speech");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        msg.Content = JsonContent.Create(new
        {
            model = _options.SpeechModel,
            input = text,
            voice = resolvedVoice,
            response_format = "mp3",
        });

        using var response = await _http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("OpenAI synthesis returned {Status}: {Body}", response.StatusCode, error);
            return new SpeechResult(Id, "audio/mpeg", Array.Empty<byte>());
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new SpeechResult(Id, "audio/mpeg", bytes);
    }

    private static string GuessAudioMime(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".webm" => "audio/webm",
            _ => "application/octet-stream",
        };
    }
}
