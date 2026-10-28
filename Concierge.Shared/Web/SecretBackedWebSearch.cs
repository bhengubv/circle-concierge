using Concierge.Shared.Settings;

namespace Concierge.Shared.Web;

/// <summary>
/// Resolves the saved search key on first use. Tool construction remains cheap and never
/// waits synchronously for the secrets file on the Blazor dispatcher.
/// </summary>
public sealed class SecretBackedWebSearch : IWebSearch
{
    private readonly Lazy<Task<IWebSearch>> _search;

    public SecretBackedWebSearch(IConciergeSecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _search = new Lazy<Task<IWebSearch>>(
            () => LoadAsync(secrets), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // A synchronous consumer can see whether first-use resolution has completed, but it
    // must never force an I/O wait. WebSearchTool uses IsConfiguredAsync before approval.
    public bool IsConfigured => _search.IsValueCreated
        && _search.Value.IsCompletedSuccessfully
        && _search.Value.Result.IsConfigured;

    public string ProviderName => "Brave Search";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => (await _search.Value.WaitAsync(cancellationToken).ConfigureAwait(false)).IsConfigured;

    public async Task<WebSearchResult> SearchAsync(
        string query, int limit, CancellationToken cancellationToken = default)
    {
        var search = await _search.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await search.SearchAsync(query, limit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IWebSearch> LoadAsync(IConciergeSecretStore secrets)
    {
        try
        {
            var saved = await secrets.LoadAsync().ConfigureAwait(false);
            return saved.TryGetValue("Brave:ApiKey", out var key) && !string.IsNullOrWhiteSpace(key)
                ? new BraveWebSearch(key)
                : new UnconfiguredWebSearch();
        }
        catch (Exception)
        {
            // Search reports setup needed; a damaged secrets file cannot stop Desktop.
            return new UnconfiguredWebSearch();
        }
    }
}
