using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Concierge.Shared.Chat;
using Microsoft.Extensions.Logging;

namespace Concierge.Chat.Cloud;

public sealed class OpenAiChatOptions
{
    public Uri BaseAddress { get; init; } = new("https://api.openai.com");
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "gpt-4o-mini";
    public float Temperature { get; init; } = 0.7f;
    public int MaxTokens { get; init; } = 1024;
}

/// <summary>
/// <see cref="IChatRuntime"/> backed by OpenAI's Chat Completions API. Works against the
/// official OpenAI endpoint or any compatible self-hosted gateway (LM Studio, llama.cpp's
/// HTTP server, vLLM, etc.) by repointing <see cref="OpenAiChatOptions.BaseAddress"/>.
/// </summary>
public sealed class OpenAiChatRuntime : IChatRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly OpenAiChatOptions _options;
    private readonly ILogger<OpenAiChatRuntime> _logger;

    public OpenAiChatRuntime(HttpClient http, OpenAiChatOptions options, ILogger<OpenAiChatRuntime> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id => "openai";

    public string EngineLabel => $"OpenAI · {_options.Model}";

    public bool IsReady => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public string StatusMessage => IsReady
        ? $"Ready · {_options.Model}"
        : "OpenAI API key not configured — set OpenAI:ApiKey in IConfiguration to enable.";

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            yield return $"[{StatusMessage}]";
            yield break;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var body = new
        {
            model = _options.Model,
            stream = true,
            temperature = _options.Temperature,
            max_tokens = _options.MaxTokens,
            messages = messages.Select(t => new { role = t.Role, content = t.Content }).ToArray(),
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("OpenAI returned {Status}: {Body}", response.StatusCode, error);
            yield return $"[OpenAI error {(int)response.StatusCode}: {Truncate(error, 240)}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var frame in ServerSentEventsReader.ReadFramesAsync(stream, cancellationToken))
        {
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.TryGetProperty("choices", out var choices)
                    && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var deltaEl)
                    && deltaEl.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    delta = contentEl.GetString();
                }
            }
            catch (JsonException)
            {
                // Heartbeat / partial frame — skip without breaking the stream.
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
