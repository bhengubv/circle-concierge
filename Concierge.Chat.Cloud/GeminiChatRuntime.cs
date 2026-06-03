using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging;

namespace Concierge.Chat.Cloud;

public sealed class GeminiChatOptions
{
    public Uri BaseAddress { get; init; } = new("https://generativelanguage.googleapis.com");
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "gemini-2.0-flash";
    public float Temperature { get; init; } = 0.7f;
    public int MaxTokens { get; init; } = 1024;
}

/// <summary>
/// <see cref="IChatRuntime"/> backed by Google's Gemini <c>streamGenerateContent</c> endpoint.
/// Gemini differs from OpenAI/Anthropic in two ways: roles use <c>model</c> rather than
/// <c>assistant</c>, and the system prompt rides on a separate <c>systemInstruction</c>
/// field with the same shape as a message content block.
/// </summary>
public sealed class GeminiChatRuntime : IChatRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly GeminiChatOptions _options;
    private readonly ILogger<GeminiChatRuntime> _logger;

    public GeminiChatRuntime(HttpClient http, GeminiChatOptions options, ILogger<GeminiChatRuntime> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id => "gemini";

    public string EngineLabel => $"Gemini · {_options.Model}";

    public bool IsReady => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public string StatusMessage => IsReady
        ? $"Ready · {_options.Model}"
        : "Gemini API key not configured — set Gemini:ApiKey in IConfiguration to enable.";

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            yield return $"[{StatusMessage}]";
            yield break;
        }

        var system = string.Join(
            "\n\n",
            messages
                .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content));
        var contents = messages
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new
            {
                role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : m.Role.ToLowerInvariant(),
                parts = new[] { new { text = m.Content } },
            })
            .ToArray();

        object body = string.IsNullOrEmpty(system)
            ? new
            {
                contents,
                generationConfig = new { temperature = _options.Temperature, maxOutputTokens = _options.MaxTokens },
            }
            : new
            {
                contents,
                systemInstruction = new { parts = new[] { new { text = system } } },
                generationConfig = new { temperature = _options.Temperature, maxOutputTokens = _options.MaxTokens },
            };

        var path = $"/v1beta/models/{Uri.EscapeDataString(_options.Model)}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(_options.ApiKey!)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Gemini returned {Status}: {Body}", response.StatusCode, error);
            yield return $"[Gemini error {(int)response.StatusCode}: {Truncate(error, 240)}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var frame in ServerSentEventsReader.ReadFramesAsync(stream, cancellationToken))
        {
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.TryGetProperty("candidates", out var candidates)
                    && candidates.GetArrayLength() > 0
                    && candidates[0].TryGetProperty("content", out var contentEl)
                    && contentEl.TryGetProperty("parts", out var partsEl)
                    && partsEl.GetArrayLength() > 0
                    && partsEl[0].TryGetProperty("text", out var textEl)
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
