using System.Text.Json;
using Concierge.Shared.Butler;
using Microsoft.Maui.Storage;

namespace Concierge.Butler;

/// <summary>Stores TGN refresh credentials with the Windows/Android platform secure-storage provider.</summary>
public sealed class MauiButlerSessionTokenStore : IButlerSessionTokenStore
{
    private const string StorageKey = "concierge.butler.session.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ButlerSessionTokens?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await SecureStorage.Default.GetAsync(StorageKey).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return JsonSerializer.Deserialize<ButlerSessionTokens>(json, JsonOptions);
    }

    public async Task SaveAsync(ButlerSessionTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(tokens, JsonOptions);
        await SecureStorage.Default.SetAsync(StorageKey, json).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(StorageKey);
        return Task.CompletedTask;
    }
}
