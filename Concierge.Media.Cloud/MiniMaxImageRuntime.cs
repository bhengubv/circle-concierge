using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Concierge.Shared.Media;
using Microsoft.Extensions.Logging;

namespace Concierge.Media.Cloud;

public sealed class MiniMaxImageOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.minimax.io");
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "MiniMax-H3-Image";
}

/// <summary>
/// MiniMax's official image API. It is an optional cloud runtime; no H3 weights are bundled.
/// The same neutral contract can later be implemented by a measured local image service.
/// </summary>
public sealed class MiniMaxImageRuntime : IImageEditingRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly MiniMaxImageOptions _options;
    private readonly ILogger<MiniMaxImageRuntime> _logger;

    public MiniMaxImageRuntime(HttpClient http, MiniMaxImageOptions options, ILogger<MiniMaxImageRuntime> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _http.BaseAddress ??= options.BaseAddress;
    }

    public string Id => "minimax-h3-image";
    public string EngineLabel => "MiniMax · H3 Image";
    public bool IsReady => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsReady ? "Ready for image creation and editing" : "MiniMax API key not configured.";

    public Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
        ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync("/v1/images/generations", request.Prompt, request.Size, request.Count,
            null, cancellationToken);
    }

    public Task<IReadOnlyList<ImageArtifact>> EditAsync(
        ImageEditRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.References is not { Count: >= 1 and <= 16 })
            throw new ArgumentException("Choose between one and sixteen reference images.", nameof(request));

        foreach (var image in request.References)
        {
            if (image.MimeType is not ("image/png" or "image/jpeg" or "image/webp" or "image/heic" or "image/heif")
                || image.Bytes is not { Length: > 0 and <= 67108864 })
                throw new ArgumentException("A reference image must be a PNG, JPEG, WebP or HEIC/HEIF file up to 64 MiB.", nameof(request));
        }

        return SendAsync("/v1/images/edits", request.Prompt, request.Size, request.Count,
            request.References, cancellationToken);
    }

    private async Task<IReadOnlyList<ImageArtifact>> SendAsync(
        string path, string prompt, int size, int count, IReadOnlyList<ImageReference>? references,
        CancellationToken cancellationToken)
    {
        if (!IsReady) return [];
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Describe the image.", nameof(prompt));

        using var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = _options.Model,
            prompt,
            size = $"{Math.Clamp(size, 1024, 4096)}x{Math.Clamp(size, 1024, 4096)}",
            n = Math.Clamp(count, 1, 10),
            response_format = "b64_json",
            output_format = "png",
            images = references?.Select(image => new
            {
                image_url = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Bytes)}",
            }).ToArray(),
        }, options: JsonOptions);

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Do not log response bodies: providers may echo private prompts or reference details.
            _logger.LogWarning("MiniMax images returned HTTP {Status}", response.StatusCode);
            return [];
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var made = new List<ImageArtifact>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                _logger.LogWarning("MiniMax image response contained an item error");
                continue;
            }

            byte[]? bytes = null;
            string? url = null;
            if (item.TryGetProperty("b64_json", out var encoded) && encoded.ValueKind == JsonValueKind.String)
            {
                try { bytes = Convert.FromBase64String(encoded.GetString()!); }
                catch (FormatException) { _logger.LogWarning("MiniMax image response contained invalid base64"); }
            }
            else if (item.TryGetProperty("url", out var address) && address.ValueKind == JsonValueKind.String)
            {
                url = address.GetString();
            }

            if (bytes is not { Length: > 0 } && string.IsNullOrWhiteSpace(url)) continue;
            made.Add(new ImageArtifact(Id, prompt, "image/png", url, bytes, DateTimeOffset.UtcNow));
        }

        return made;
    }
}
