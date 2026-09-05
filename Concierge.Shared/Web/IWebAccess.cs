namespace Concierge.Shared.Web;

/// <summary>
/// Reaching the network, which Concierge otherwise does not do.
///
/// This is the seam the web tools sit on. It exists as an interface for the
/// usual reason — tests must not make real requests — and because search needs
/// a provider that a user configures, while fetching a named URL does not.
/// </summary>
public interface IWebAccess
{
    /// <summary>Fetches one URL and returns it as readable text.</summary>
    Task<WebFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>
/// Searching the web, which needs a provider and a key.
///
/// Separate from <see cref="IWebAccess"/> because the two fail differently: a
/// fetch works out of the box, and a search cannot happen at all until somebody
/// configures a provider. Saying that plainly is better than a tool that
/// silently returns nothing.
/// </summary>
public interface IWebSearch
{
    /// <summary>Whether a provider is configured. False means every call will
    /// fail, and the tool says so rather than trying.</summary>
    bool IsConfigured { get; }

    /// <summary>Human-readable name of the provider, for the settings panel.</summary>
    string ProviderName { get; }

    Task<WebSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// What came back from a fetch.
///
/// <paramref name="Text"/> is content from the open internet and is untrusted:
/// a page can carry text addressed to the model rather than to the reader. The
/// tool that returns it labels it as such, and nothing downstream should treat
/// it as instruction.
/// </summary>
public sealed record WebFetchResult(
    bool Success,
    string Url,
    string? Title,
    string Text,
    int BytesRead,
    bool Truncated,
    string? FailureMessage = null);

public sealed record WebSearchResult(
    bool Success,
    string Query,
    IReadOnlyList<WebSearchHit> Hits,
    string? FailureMessage = null);

public sealed record WebSearchHit(string Title, string Url, string Snippet);
