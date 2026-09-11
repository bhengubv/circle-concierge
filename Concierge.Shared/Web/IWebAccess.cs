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

    /// <summary>
    /// Fetches one URL and returns the bytes, for a file rather than a page.
    ///
    /// On this interface rather than beside the thing that needs it, so there is
    /// **one** set of rules about what this program may reach. A second fetcher
    /// with its own idea of which addresses are allowed is how a guard comes to
    /// protect one path and not the other, and the path it misses is always the
    /// newer one.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="expectedType">
    /// The media type this is willing to accept, as a prefix — "audio/" for a
    /// track. A server answering with something else is refused rather than
    /// downloaded and guessed at.
    /// </param>
    /// <param name="maxBytes">The most it will take before giving up.</param>
    Task<WebBytesResult> FetchBytesAsync(
        string url, string expectedType, int maxBytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// What came back from a fetch of bytes.
/// </summary>
/// <param name="Success">Whether there is anything here.</param>
/// <param name="Url">Where it finally came from, after any redirects.</param>
/// <param name="MediaType">What the server said it is.</param>
/// <param name="Bytes">The content, or empty.</param>
/// <param name="Problem">Why not, in words a person can act on.</param>
public sealed record WebBytesResult(
    bool Success, string Url, string MediaType, byte[] Bytes, string? Problem)
{
    /// <summary>The content as a data URI, which is how a design carries a file.</summary>
    public string AsDataUri => $"data:{MediaType};base64,{Convert.ToBase64String(Bytes)}";
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
