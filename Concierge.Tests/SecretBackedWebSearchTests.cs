using System.Text.Json.Nodes;
using Concierge.Shared.Settings;
using Concierge.Shared.Tools;
using Concierge.Shared.Web;

namespace Concierge.Tests;

public sealed class SecretBackedWebSearchTests
{
    [Fact]
    public async Task Search_key_load_is_async_and_shared_by_concurrent_callers()
    {
        var store = new DelayedSecrets();
        var search = new SecretBackedWebSearch(store);
        Assert.False(search.IsConfigured);
        Assert.Equal(0, store.Loads);

        var first = search.IsConfiguredAsync();
        var second = search.IsConfiguredAsync();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, store.Loads);

        store.Complete(new Dictionary<string, string> { ["Brave:ApiKey"] = "saved-key" });
        Assert.True(await first);
        Assert.True(await second);
        Assert.True(search.IsConfigured);
    }

    [Fact]
    public async Task Missing_key_reports_setup_needed_without_approval()
    {
        var store = new DelayedSecrets();
        var search = new SecretBackedWebSearch(store);
        var tool = new WebSearchTool(search, UnavailableToolApprovalService.Instance);
        var attempt = tool.InvokeAsync(JsonNode.Parse("""{"query":"hello"}"""));
        store.Complete(new Dictionary<string, string>());

        var result = await attempt;
        Assert.False(result.Success);
        Assert.Contains("Settings", result.FailureMessage);
        Assert.Equal(1, store.Loads);
    }

    private sealed class DelayedSecrets : IConciergeSecretStore
    {
        private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>> _loaded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Path => "test-only";
        public int Loads { get; private set; }
        public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            return _loaded.Task;
        }
        public Task SaveAsync(IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public void Complete(IReadOnlyDictionary<string, string> secrets) => _loaded.SetResult(secrets);
    }
}
