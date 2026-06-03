using System.Net.Http.Headers;
using Concierge.Shared.Media;
using Microsoft.Extensions.Logging;

namespace Concierge.Media.Cloud;

public sealed class StabilityImageOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.stability.ai");
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "sd3.5-large";
    public string OutputFormat { get; init; } = "png";
}

/// <summary>
/// <see cref="IImageRuntime"/> backed by Stability AI's REST endpoint. The Stability API
/// streams image bytes back directly (no remote URL), so we return them inline. Single
/// image per call — to honor <see cref="ImageGenerationRequest.Count"/> we loop on the
/// caller's behalf so the UI is shape-stable across providers.
/// </summary>
public sealed class StabilityImageRuntime : IImageRuntime
{
    private readonly HttpClient _http;
    private readonly StabilityImageOptions _options;
    private readonly ILogger<StabilityImageRuntime> _logger;

    public StabilityImageRuntime(HttpClient http, StabilityImageOptions options, ILogger<StabilityImageRuntime> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id => "stability";
    public string EngineLabel => $"Stability AI · {_options.Model}";
    public bool IsReady => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsReady ? $"Ready · {_options.Model}" : "Stability AI API key not configured — set Stability:ApiKey to enable.";

    public async Task<IReadOnlyList<ImageArtifact>> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            return Array.Empty<ImageArtifact>();
        }

        var artifacts = new List<ImageArtifact>();
        var count = Math.Clamp(request.Count, 1, 4);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var msg = new HttpRequestMessage(HttpMethod.Post, $"/v2beta/stable-image/generate/sd3");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue($"image/{_options.OutputFormat}"));

            var form = new MultipartFormDataContent
            {
                { new StringContent(request.Prompt), "prompt" },
                { new StringContent(_options.OutputFormat), "output_format" },
                { new StringContent(_options.Model), "model" },
            };
            if (!string.IsNullOrEmpty(request.NegativePrompt))
            {
                form.Add(new StringContent(request.NegativePrompt), "negative_prompt");
            }
            msg.Content = form;

            using var response = await _http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Stability returned {Status}: {Body}", response.StatusCode, error);
                continue;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            artifacts.Add(new ImageArtifact(
                RuntimeId: Id,
                Prompt: request.Prompt,
                MimeType: $"image/{_options.OutputFormat}",
                Url: null,
                Bytes: bytes,
                GeneratedAtUtc: DateTimeOffset.UtcNow));
        }

        return artifacts;
    }
}
