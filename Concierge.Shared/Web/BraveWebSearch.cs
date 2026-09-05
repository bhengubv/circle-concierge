using System.Net.Http.Headers;
using System.Text.Json;

namespace Concierge.Shared.Web;

/// <summary>
/// Search, through a provider the user configures.
///
/// Brave is the default because it publishes a documented API with a free tier
/// and does not require scraping somebody's results page. Scraping was the
/// other option and was rejected: it breaks whenever the page changes, and it
/// takes a product that is careful about what it does on your behalf and has it
/// pretend to be a browser.
///
/// With no key this reports that it is not configured rather than failing per
/// call. The tool then says so once, which is the same thing the Images room
/// does when no image runtime is registered.
/// </summary>
public sealed class BraveWebSearch : IWebSearch
{
    private const string Endpoint = "https://api.search.brave.com/res/v1/web/search";

    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public BraveWebSearch(string? apiKey, HttpClient? http = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public bool IsConfigured => _apiKey is not null;

    public string ProviderName => "Brave Search";

    public async Task<WebSearchResult> SearchAsync(
        string query, int limit, CancellationToken cancellationToken = default)
    {
        if (_apiKey is null)
        {
            return new WebSearchResult(false, query, Array.Empty<WebSearchHit>(),
                "No search provider is set up. Add a Brave Search key in Settings, under Keys.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new WebSearchResult(false, query, Array.Empty<WebSearchHit>(), "Nothing to search for.");
        }

        var count = Math.Clamp(limit, 1, 10);
        var url = $"{Endpoint}?q={Uri.EscapeDataString(query)}&count={count}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Subscription-Token", _apiKey);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // The key is never echoed back, whatever the provider said.
                return new WebSearchResult(false, query, Array.Empty<WebSearchHit>(),
                    $"The search provider answered {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new WebSearchResult(true, query, Parse(body));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebSearchResult(false, query, Array.Empty<WebSearchHit>(), "The search did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new WebSearchResult(false, query, Array.Empty<WebSearchHit>(), $"Could not reach the search provider: {ex.Message}");
        }
    }

    internal static IReadOnlyList<WebSearchHit> Parse(string json)
    {
        var hits = new List<WebSearchHit>();

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("web", out var web)
                || !web.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return hits;
            }

            foreach (var result in results.EnumerateArray())
            {
                var title = Text(result, "title");
                var link = Text(result, "url");

                if (string.IsNullOrWhiteSpace(link))
                {
                    continue;
                }

                hits.Add(new WebSearchHit(
                    string.IsNullOrWhiteSpace(title) ? link : title,
                    link,
                    // Brave returns the snippet with <strong> around the matched
                    // terms; the model wants the words, not the markup.
                    HttpWebAccess.ToText(Text(result, "description"))));
            }
        }
        catch (JsonException)
        {
            // A provider that changed its shape is a provider that returns
            // nothing, not one that takes the turn down.
        }

        return hits;
    }

    private static string Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>Stands in when nothing is configured, so the tool can still be
/// registered and still explain itself.</summary>
public sealed class UnconfiguredWebSearch : IWebSearch
{
    public bool IsConfigured => false;

    public string ProviderName => "None";

    public Task<WebSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(new WebSearchResult(false, query, Array.Empty<WebSearchHit>(),
            "No search provider is set up. Add a Brave Search key in Settings, under Keys."));
}
