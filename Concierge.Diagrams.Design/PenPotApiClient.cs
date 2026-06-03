using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Concierge.Diagrams.Design;

/// <summary>
/// Configuration for the PenPot REST client. PenPot is open-source — the same client points
/// at <c>https://design.penpot.app</c> (the hosted cloud), a self-hosted instance, or a local
/// development server, depending on what the user has access to.
/// </summary>
public sealed class PenPotApiOptions
{
    /// <summary>Base URL of the PenPot instance. No trailing slash.</summary>
    public Uri BaseAddress { get; init; } = new("https://design.penpot.app");

    /// <summary>
    /// User access token, generated from PenPot's user profile (Settings → Access tokens).
    /// Sent as <c>Authorization: Token {AccessToken}</c>.
    /// </summary>
    public string? AccessToken { get; init; }
}

/// <summary>
/// Minimal client for PenPot's public RPC API. PenPot's API is JSON-RPC-style — every call
/// is a POST to <c>/api/rpc/command/{name}</c> with a JSON body. The contract is small: we
/// only need the file fetch + page fetch endpoints to import a design.
/// </summary>
public sealed class PenPotApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly PenPotApiOptions _options;

    public PenPotApiClient(HttpClient http, PenPotApiOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }

        if (!string.IsNullOrEmpty(_options.AccessToken)
            && !_http.DefaultRequestHeaders.Contains("Authorization"))
        {
            _http.DefaultRequestHeaders.Add("Authorization", $"Token {_options.AccessToken}");
        }
    }

    /// <summary>Fetches a single PenPot file by its UUID.</summary>
    public async Task<PenPotFile> GetFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "/api/rpc/command/get-file",
            new { id = fileId },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var file = await response.Content
            .ReadFromJsonAsync<PenPotFile>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return file ?? throw new InvalidOperationException("PenPot returned an empty file body.");
    }

    /// <summary>
    /// Parses a JSON file payload directly — convenient for cases where the host has the
    /// PenPot file body already (e.g. user pasted exported JSON). Same shape as the API
    /// response so we don't need to fork the parser.
    /// </summary>
    public static PenPotFile ParseFileJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var file = JsonSerializer.Deserialize<PenPotFile>(json, JsonOptions)
            ?? throw new InvalidOperationException("PenPot JSON did not deserialise into a file.");
        return file;
    }
}

// PenPot's data model is recursive — pages contain shape trees; each shape can be a frame
// (container), group, or leaf. We only model the fields we actually need to summarise the
// document; everything else is captured in the catch-all `Extra` dictionary so future
// features can read it without re-parsing.

public sealed record PenPotFile(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("data")] PenPotFileData? Data);

public sealed record PenPotFileData(
    [property: JsonPropertyName("pages")] IReadOnlyList<Guid>? Pages,
    [property: JsonPropertyName("pages-index")] IReadOnlyDictionary<string, PenPotPage>? PagesIndex);

public sealed record PenPotPage(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("objects")] IReadOnlyDictionary<string, PenPotShape>? Objects);

public sealed record PenPotShape(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("parent-id")] string? ParentId,
    [property: JsonPropertyName("frame-id")] string? FrameId,
    [property: JsonPropertyName("shapes")] IReadOnlyList<string>? Children);
