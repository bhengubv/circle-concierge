using System.Collections.Concurrent;

namespace Concierge.Shared.Providers;

/// <summary>One model an endpoint says it serves.</summary>
/// <param name="Id">The identifier a request must use.</param>
/// <param name="DisplayName">What to show a person choosing.</param>
public sealed record DiscoveredModel(string Id, string DisplayName);

/// <summary>What to ask, and where.</summary>
/// <param name="ProviderId">Which adapter is being configured.</param>
/// <param name="BaseUrl">The endpoint to interrogate.</param>
/// <param name="ApiKey">
/// A key used for this one interrogation and never stored. Discovery happens while a
/// provider is being added, before there is anywhere to keep it.
/// </param>
public sealed record ModelDiscoveryRequest(string ProviderId, string BaseUrl, string? ApiKey = null);

/// <summary>What the endpoint said.</summary>
/// <param name="Success">Whether it answered.</param>
/// <param name="Models">What it offers, deduplicated. Never null.</param>
/// <param name="Error">Why it did not answer, when it did not.</param>
public sealed record ModelDiscoveryResult(bool Success, IReadOnlyList<DiscoveredModel> Models, string? Error);

/// <summary>
/// Asks a provider endpoint which models it serves.
/// </summary>
/// <remarks>
/// Concierge lets people bring their own keys across ten provider families, each naming
/// models differently and renaming them without notice. Asking the endpoint is the difference
/// between a settings screen that works and one that fails on a typo the user cannot see.
/// </remarks>
public interface IModelDiscovery
{
    /// <summary>Ask one endpoint what it offers.</summary>
    Task<ModelDiscoveryResult> DiscoverAsync(ModelDiscoveryRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovery over a caller-supplied interrogation, with the validation every provider needs
/// applied to whatever comes back.
/// </summary>
/// <remarks>
/// The per-provider HTTP call differs enough that sharing it would help nobody; what is worth
/// sharing is the handling of the reply. Endpoints return duplicates, blank ids, and
/// occasionally an error page with a 200 status.
/// </remarks>
public sealed class DelegatingModelDiscovery : IModelDiscovery
{
    private readonly Func<ModelDiscoveryRequest, Task<IReadOnlyList<DiscoveredModel>>> _interrogate;

    public DelegatingModelDiscovery(Func<ModelDiscoveryRequest, Task<IReadOnlyList<DiscoveredModel>>> interrogate)
        => _interrogate = interrogate ?? throw new ArgumentNullException(nameof(interrogate));

    /// <inheritdoc />
    public async Task<ModelDiscoveryResult> DiscoverAsync(
        ModelDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return new ModelDiscoveryResult(false, [], "No endpoint was given to ask.");
        }

        try
        {
            var offered = await _interrogate(request).ConfigureAwait(false) ?? [];

            // Endpoints repeat themselves and occasionally return entries with no id. A
            // blank id cannot be used in a request, so offering it would only produce a
            // failure later, further from the cause.
            var models = offered
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            return new ModelDiscoveryResult(true, models, null);
        }
        catch (OperationCanceledException)
        {
            return new ModelDiscoveryResult(false, [], "The request was stopped.");
        }
        catch (Exception exception)
        {
            // A user adding a provider needs to see what went wrong. Throwing here would
            // surface as an unhandled error in a settings screen.
            return new ModelDiscoveryResult(false, [], exception.Message);
        }
    }
}

/// <summary>
/// Somewhere files and processes can be worked with.
/// </summary>
/// <remarks>
/// For other harnesses the non-local case is a cloud sandbox. Here the natural remote is a
/// paired phone on the mesh — a device with power and signal working on behalf of one
/// without. The seam is the same either way; that is what makes it worth having.
/// </remarks>
public interface IExecutionProvider
{
    /// <summary>Whether work runs on this device.</summary>
    bool IsLocal { get; }

    /// <summary>A short description of where work runs, for showing to a person.</summary>
    Task<string> DescribeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Work that happens on this device.</summary>
public sealed class LocalExecutionProvider : IExecutionProvider
{
    /// <inheritdoc />
    public bool IsLocal => true;

    /// <inheritdoc />
    public Task<string> DescribeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult("this device");
}

/// <summary>The execution providers a deployment has.</summary>
public sealed class ExecutionProviderRegistry
{
    private readonly ConcurrentDictionary<string, IExecutionProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Always present: a device can always act on itself, and a caller that has to
            // check whether local execution exists has one branch too many.
            ["local"] = new LocalExecutionProvider(),
        };

    /// <summary>Every provider registered.</summary>
    public IReadOnlyCollection<string> Names => _providers.Keys.ToList();

    /// <summary>Register somewhere work can happen.</summary>
    public void Register(string name, IExecutionProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);
        _providers[name] = provider;
    }

    /// <summary>Get a provider by name.</summary>
    /// <exception cref="InvalidOperationException">No provider by that name is registered.</exception>
    public IExecutionProvider Require(string name)
        => _providers.TryGetValue(name ?? string.Empty, out var provider)
            ? provider
            : throw new InvalidOperationException($"No execution provider named '{name}' is registered.");
}
