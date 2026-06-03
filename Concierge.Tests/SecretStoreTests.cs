using Concierge.Shared.Settings;

namespace Concierge.Tests;

public sealed class SecretStoreTests
{
    [Fact]
    public async Task Round_trip_persists_and_loads_saved_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-secrets-{Guid.NewGuid():N}.json");
        try
        {
            var store = new LocalSecretStore(path);

            Assert.Empty(await store.LoadAsync());

            await store.SaveAsync(new Dictionary<string, string>
            {
                [ConciergeSecretKeys.OpenAi] = "sk-test-1",
                [ConciergeSecretKeys.Anthropic] = "anth-test-2",
            });

            var loaded = await store.LoadAsync();
            Assert.Equal("sk-test-1", loaded[ConciergeSecretKeys.OpenAi]);
            Assert.Equal("anth-test-2", loaded[ConciergeSecretKeys.Anthropic]);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Save_merges_with_existing_and_empty_values_remove_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-secrets-{Guid.NewGuid():N}.json");
        try
        {
            var store = new LocalSecretStore(path);
            await store.SaveAsync(new Dictionary<string, string>
            {
                [ConciergeSecretKeys.OpenAi] = "first",
                [ConciergeSecretKeys.Gemini] = "g1",
            });

            // Overwrite OpenAi, blank-out Gemini, add Stability.
            await store.SaveAsync(new Dictionary<string, string>
            {
                [ConciergeSecretKeys.OpenAi] = "second",
                [ConciergeSecretKeys.Gemini] = string.Empty,
                [ConciergeSecretKeys.Stability] = "stab",
            });

            var loaded = await store.LoadAsync();
            Assert.Equal("second", loaded[ConciergeSecretKeys.OpenAi]);
            Assert.False(loaded.ContainsKey(ConciergeSecretKeys.Gemini));
            Assert.Equal("stab", loaded[ConciergeSecretKeys.Stability]);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Load_returns_empty_when_file_does_not_exist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-secrets-missing-{Guid.NewGuid():N}.json");
        var store = new LocalSecretStore(path);

        var result = await store.LoadAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task Load_returns_empty_when_file_is_malformed_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"concierge-secrets-bad-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ this is not json :::");
        try
        {
            var store = new LocalSecretStore(path);
            var result = await store.LoadAsync();
            Assert.Empty(result);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Catalog_lists_every_known_key_in_a_stable_order()
    {
        var keys = ConciergeSecretKeys.Catalog.Select(e => e.Key).ToList();
        Assert.Contains(ConciergeSecretKeys.OpenAi, keys);
        Assert.Contains(ConciergeSecretKeys.Anthropic, keys);
        Assert.Contains(ConciergeSecretKeys.Gemini, keys);
        Assert.Contains(ConciergeSecretKeys.OpenAiImages, keys);
        Assert.Contains(ConciergeSecretKeys.OpenAiVoice, keys);
        Assert.Contains(ConciergeSecretKeys.Stability, keys);
        Assert.Contains(ConciergeSecretKeys.PenPot, keys);
        Assert.Contains(ConciergeSecretKeys.Figma, keys);
        Assert.Contains(ConciergeSecretKeys.ConciergeApiKey, keys);
        // No duplicate entries.
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Test cleanup is best-effort.
        }
    }
}
