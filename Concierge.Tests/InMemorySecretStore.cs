using Concierge.Shared.Settings;

namespace Concierge.Tests;

/// <summary>Isolated API-key storage for component tests; never reads user files.</summary>
internal sealed class InMemorySecretStore : IConciergeSecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public string Path => "(test)";

    public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(_secrets, StringComparer.Ordinal));

    public Task SaveAsync(IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default)
    {
        foreach (var (key, value) in secrets)
        {
            if (string.IsNullOrEmpty(value))
                _secrets.Remove(key);
            else
                _secrets[key] = value;
        }

        return Task.CompletedTask;
    }
}
