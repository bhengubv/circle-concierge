using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Concierge.Shared.Media;
using Microsoft.Extensions.Logging;

namespace Concierge.Media.Cloud;

public sealed class OpenAiImageOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.openai.com");
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "dall-e-3";
}

/// <summary>
/// <see cref="IImageRuntime"/> backed by OpenAI's image-generation endpoint. The API returns
/// a base64 payload by default; we keep <c>response_format=url</c> so the UI can render
/// directly from the URL without storing megabytes in DOM.
/// </summary>
public sealed class OpenAiImageRuntime : IImageRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly OpenAiImageOptions _options;
    private readonly ILogger<OpenAiImageRuntime> _logger;

    public OpenAiImageRuntime(HttpClient http, OpenAiImageOptions options, ILogger<OpenAiImageRuntime> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id => "openai-images";
    public string EngineLabel => $"OpenAI · {_options.Model}";
    public bool IsReady => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsReady ? $"Ready · {_options.Model}" : "OpenAI API key not configured — set OpenAI:ApiKey to enable.";

    public async Task<IReadOnlyList<ImageArtifact>> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            return Array.Empty<ImageArtifact>();
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        msg.Content = JsonContent.Create(new
        {
            model = _options.Model,
            prompt = request.Prompt,
            n = Math.Clamp(request.Count, 1, 4),
            size = $"{request.Size}x{request.Size}",
            response_format = "url",
        }, options: JsonOptions);

        using var response = await _http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("OpenAI images returned {Status}: {Body}", response.StatusCode, error);
            return Array.Empty<ImageArtifact>();
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var artifacts = new List<ImageArtifact>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    artifacts.Add(new ImageArtifact(
                        RuntimeId: Id,
                        Prompt: request.Prompt,
                        MimeType: "image/png",
                        Url: url.GetString(),
                        Bytes: null,
                        GeneratedAtUtc: DateTimeOffset.UtcNow));
                }
            }
        }

        return artifacts;
    }
}
