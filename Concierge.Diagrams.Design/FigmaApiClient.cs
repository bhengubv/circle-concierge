using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concierge.Diagrams.Design;

/// <summary>
/// Configuration for the Figma REST client. The personal access token is created from the
/// user's Figma account settings (Settings → Personal access tokens). Tokens scope to the
/// user — Concierge stores them per-user once the auth layer ships.
/// </summary>
public sealed class FigmaApiOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.figma.com");

    /// <summary>
    /// Personal access token. Sent as <c>X-Figma-Token</c>. Optional only for unauthenticated
    /// public-file reads (rare); production callers should always set this.
    /// </summary>
    public string? AccessToken { get; init; }
}

/// <summary>
/// Minimal Figma REST client. Only models the slice needed to import a design — file fetch
/// returns the canvas + node tree, which the runtime then flattens into <see cref="Shared.Diagrams.DiagramElement"/>
/// rows. The Figma JSON is well-typed and stable; the model below is intentionally
/// narrow (no styling, no exports — those land when the agent asks for them).
/// </summary>
public sealed class FigmaApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public FigmaApiClient(HttpClient http, FigmaApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http ?? throw new ArgumentNullException(nameof(http));

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }

        if (!string.IsNullOrEmpty(options.AccessToken)
            && !_http.DefaultRequestHeaders.Contains("X-Figma-Token"))
        {
            _http.DefaultRequestHeaders.Add("X-Figma-Token", options.AccessToken);
        }
    }

    /// <summary>
    /// Fetches a Figma file by its <c>file-key</c> (the segment after <c>/file/</c> in a
    /// Figma URL).
    /// </summary>
    public async Task<FigmaFile> GetFileAsync(string fileKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileKey);
        using var response = await _http.GetAsync($"/v1/files/{Uri.EscapeDataString(fileKey)}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var file = await response.Content
            .ReadFromJsonAsync<FigmaFile>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return file ?? throw new InvalidOperationException("Figma returned an empty file body.");
    }

    /// <summary>
    /// Parses a Figma JSON body — convenient for testing or when the caller fetched the
    /// JSON via a different pipeline.
    /// </summary>
    public static FigmaFile ParseFileJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<FigmaFile>(json, JsonOptions)
            ?? throw new InvalidOperationException("Figma JSON did not deserialise into a file.");
    }
}

public sealed record FigmaFile(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("lastModified")] DateTimeOffset? LastModified,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("document")] FigmaNode? Document);

public sealed record FigmaNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("children")] IReadOnlyList<FigmaNode>? Children);
