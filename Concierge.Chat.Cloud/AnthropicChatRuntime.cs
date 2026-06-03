using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging;

namespace Concierge.Chat.Cloud;

public sealed class AnthropicChatOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.anthropic.com");
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "claude-3-5-sonnet-latest";
    public float Temperature { get; init; } = 0.7f;
    public int MaxTokens { get; init; } = 1024;

    /// <summary>
    /// Anthropic requires an explicit <c>anthropic-version</c> header. Bumping is API-shape
    /// negotiation; keep current unless the SDK calls out an incompatibility.
    /// </summary>
    public string ApiVersion { get; init; } = "2023-06-01";
}

/// <summary>
/// <see cref="IChatRuntime"/> backed by Anthropic's Messages API. The Anthropic protocol
/// differs from OpenAI in two ways: (1) the system prompt is a top-level field, not a
/// <c>role: "system"</c> entry in messages; (2) streamed deltas come back as
/// <c>content_block_delta</c> events whose payload is <c>{ delta: { type, text } }</c>.
/// </summary>
public sealed class AnthropicChatRuntime : IChatRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AnthropicChatOptions _options;
    private readonly ILogger<AnthropicChatRuntime> _logger;

    public AnthropicChatRuntime(HttpClient http, AnthropicChatOptions options, ILogger<AnthropicChatRuntime> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id => "anthropic";

    public string EngineLabel => $"Anthropic · {_options.Model}";

    public bool IsReady => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public string StatusMessage => IsReady
        ? $"Ready · {_options.Model}"
        : "Anthropic API key not configured — set Anthropic:ApiKey in IConfiguration to enable.";

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            yield return $"[{StatusMessage}]";
            yield break;
        }

        // Anthropic wants system prompt out-of-band; split user / assistant from system.
        var system = string.Join(
            "\n\n",
            messages
                .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content));
        var chat = messages
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new { role = m.Role.ToLowerInvariant(), content = m.Content })
            .ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", _options.ApiVersion);

        object body = string.IsNullOrEmpty(system)
            ? new
            {
                model = _options.Model,
                max_tokens = _options.MaxTokens,
                temperature = _options.Temperature,
                stream = true,
                messages = chat,
            }
            : new
            {
                model = _options.Model,
                max_tokens = _options.MaxTokens,
                temperature = _options.Temperature,
                stream = true,
                system,
                messages = chat,
            };
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Anthropic returned {Status}: {Body}", response.StatusCode, error);
            yield return $"[Anthropic error {(int)response.StatusCode}: {Truncate(error, 240)}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var frame in ServerSentEventsReader.ReadFramesAsync(stream, cancellationToken))
        {
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.TryGetProperty("type", out var typeEl)
                    && typeEl.GetString() == "content_block_delta"
                    && doc.RootElement.TryGetProperty("delta", out var deltaEl)
                    && deltaEl.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    delta = textEl.GetString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
