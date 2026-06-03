using System.Net;
using System.Net.Http;
using System.Text;
using Concierge.Media.Cloud;
using Concierge.Shared.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Tests;

public sealed class MediaRuntimeTests
{
    // ------------------------------------------------------------------ Images

    [Fact]
    public async Task OpenAi_image_runtime_returns_url_artifacts_from_data_array()
    {
        var json = """
        { "data": [
          { "url": "https://example.com/one.png" },
          { "url": "https://example.com/two.png" }
        ] }
        """;
        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal("Bearer img-key", req.Headers.Authorization?.ToString());
            Assert.Equal("/v1/images/generations", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") };
        var runtime = new OpenAiImageRuntime(http, new OpenAiImageOptions { ApiKey = "img-key" }, NullLogger<OpenAiImageRuntime>.Instance);

        var artifacts = await runtime.GenerateAsync(new ImageGenerationRequest("a brand-blue diagram", Count: 2));

        Assert.Equal("openai-images", runtime.Id);
        Assert.True(runtime.IsReady);
        Assert.Equal(2, artifacts.Count);
        Assert.All(artifacts, a => Assert.NotNull(a.Url));
        Assert.Equal("https://example.com/one.png", artifacts[0].Url);
    }

    [Fact]
    public async Task OpenAi_image_runtime_without_key_returns_empty_without_calling()
    {
        using var http = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("HTTP must not be called")));
        var runtime = new OpenAiImageRuntime(http, new OpenAiImageOptions { ApiKey = null }, NullLogger<OpenAiImageRuntime>.Instance);

        Assert.False(runtime.IsReady);
        Assert.Empty(await runtime.GenerateAsync(new ImageGenerationRequest("anything")));
    }

    [Fact]
    public async Task Stability_image_runtime_returns_inline_bytes_per_request_count()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var seenForm = new List<string>();
        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal("Bearer stab-key", req.Headers.Authorization?.ToString());
            Assert.Equal("/v2beta/stable-image/generate/sd3", req.RequestUri!.AbsolutePath);
            seenForm.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pngBytes),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.stability.ai") };
        var runtime = new StabilityImageRuntime(http, new StabilityImageOptions { ApiKey = "stab-key" }, NullLogger<StabilityImageRuntime>.Instance);

        var artifacts = await runtime.GenerateAsync(new ImageGenerationRequest("brand blue", Count: 2, NegativePrompt: "no orange"));

        Assert.Equal(2, artifacts.Count);
        Assert.All(artifacts, a => Assert.NotNull(a.Bytes));
        Assert.All(artifacts, a => Assert.Equal(pngBytes, a.Bytes));
        // Multipart body contains the negative prompt — verifies the form-field was emitted.
        Assert.Contains(seenForm, body => body.Contains("no orange", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ Voice

    [Fact]
    public async Task OpenAi_voice_transcribe_returns_text_from_whisper_json_response()
    {
        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal("Bearer voice-key", req.Headers.Authorization?.ToString());
            Assert.Equal("/v1/audio/transcriptions", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                { "text": "hello brand blue", "language": "en" }
                """, Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") };
        var runtime = new OpenAiVoiceRuntime(http, new OpenAiVoiceOptions { ApiKey = "voice-key" }, NullLogger<OpenAiVoiceRuntime>.Instance);

        using var audio = new MemoryStream(new byte[] { 0x52, 0x49, 0x46, 0x46 });
        var result = await runtime.TranscribeAsync(audio, "sample.wav");

        Assert.Equal("openai-voice", runtime.Id);
        Assert.True(runtime.SupportsTranscription);
        Assert.True(runtime.SupportsSynthesis);
        Assert.Equal("hello brand blue", result.Text);
        Assert.Equal("en", result.Language);
    }

    [Fact]
    public async Task OpenAi_voice_synthesize_returns_mp3_bytes_with_resolved_voice()
    {
        string? sentBody = null;
        var audio = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        var handler = new StubHandler((req, _) =>
        {
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Equal("/v1/audio/speech", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") };
        var runtime = new OpenAiVoiceRuntime(http, new OpenAiVoiceOptions { ApiKey = "voice-key", DefaultVoice = "nova" }, NullLogger<OpenAiVoiceRuntime>.Instance);

        var result = await runtime.SynthesizeAsync("hello", voice: "echo");

        Assert.Equal(audio, result.Audio);
        Assert.Equal("audio/mpeg", result.MimeType);
        Assert.NotNull(sentBody);
        Assert.Contains("\"voice\":\"echo\"", sentBody);
    }

    [Fact]
    public void Null_image_and_voice_runtimes_report_not_ready_with_helpful_status()
    {
        var image = new NullImageRuntime();
        var voice = new NullVoiceRuntime();

        Assert.False(image.IsReady);
        Assert.Contains("OpenAI:ApiKey", image.StatusMessage, StringComparison.Ordinal);
        Assert.False(voice.IsReady);
        Assert.False(voice.SupportsTranscription);
        Assert.False(voice.SupportsSynthesis);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _send;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_send(request, cancellationToken));
    }
}
