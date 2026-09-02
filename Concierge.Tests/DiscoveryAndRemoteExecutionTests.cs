using Concierge.Shared.Providers;

namespace Concierge.Tests;

/// <summary>
/// What model discovery must do (parity feature 36): ask an endpoint what it serves, so a
/// user adding their own key does not have to type a model id correctly.
/// </summary>
/// <remarks>
/// Concierge lets people bring their own keys for ten providers. Every one of them names
/// models differently and renames them without notice. Asking the endpoint is the difference
/// between a settings screen that works and one that silently fails on a typo.
/// </remarks>
public sealed class ModelDiscoveryTests
{
    [Fact]
    public async Task An_endpoint_that_lists_models_is_read()
    {
        var discovery = new DelegatingModelDiscovery(_ => Task.FromResult<IReadOnlyList<DiscoveredModel>>(
        [
            new DiscoveredModel("qwen3-4b", "Qwen3 4B"),
            new DiscoveredModel("qwen3-8b", "Qwen3 8B"),
        ]));

        var result = await discovery.DiscoverAsync(new ModelDiscoveryRequest("openai", "https://example.test"));

        Assert.True(result.Success);
        Assert.Equal(2, result.Models.Count);
    }

    [Fact]
    public async Task A_discovered_model_keeps_its_identifier()
    {
        var discovery = new DelegatingModelDiscovery(_ => Task.FromResult<IReadOnlyList<DiscoveredModel>>(
            [new DiscoveredModel("qwen3-4b", "Qwen3 4B")]));

        var result = await discovery.DiscoverAsync(new ModelDiscoveryRequest("openai", "https://example.test"));

        Assert.Equal("qwen3-4b", result.Models[0].Id);
    }

    [Fact]
    public async Task Duplicate_models_are_returned_once()
    {
        var discovery = new DelegatingModelDiscovery(_ => Task.FromResult<IReadOnlyList<DiscoveredModel>>(
        [
            new DiscoveredModel("qwen3-4b", "Qwen3 4B"),
            new DiscoveredModel("qwen3-4b", "Qwen3 4B (again)"),
        ]));

        var result = await discovery.DiscoverAsync(new ModelDiscoveryRequest("openai", "https://example.test"));

        Assert.Single(result.Models);
    }

    [Fact]
    public async Task A_model_without_an_identifier_is_dropped()
    {
        var discovery = new DelegatingModelDiscovery(_ => Task.FromResult<IReadOnlyList<DiscoveredModel>>(
        [
            new DiscoveredModel("  ", "nameless"),
            new DiscoveredModel("real", "Real"),
        ]));

        var result = await discovery.DiscoverAsync(new ModelDiscoveryRequest("openai", "https://example.test"));

        Assert.Single(result.Models);
    }

    [Fact]
    public async Task An_endpoint_that_fails_is_reported_not_thrown()
    {
        var discovery = new DelegatingModelDiscovery(_ => throw new HttpRequestException("no route to host"));

        var result = await discovery.DiscoverAsync(new ModelDiscoveryRequest("openai", "https://example.test"));

        Assert.False(result.Success);
        Assert.Contains("no route to host", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_without_an_endpoint_is_refused()
    {
        var discovery = new DelegatingModelDiscovery(_ => Task.FromResult<IReadOnlyList<DiscoveredModel>>([]));

        var result = await discovery.DiscoverAsync(new ModelDiscoveryRequest("openai", "  "));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Discovery_never_returns_null_models()
    {
        var discovery = new DelegatingModelDiscovery(_ => Task.FromResult<IReadOnlyList<DiscoveredModel>>([]));

        var result = await discovery.DiscoverAsync(new ModelDiscoveryRequest("openai", "https://example.test"));

        Assert.NotNull(result.Models);
    }
}

/// <summary>
/// What the execution seam must do (parity feature 43): let file and process work happen
/// somewhere other than this device, without any caller knowing.
/// </summary>
/// <remarks>
/// For every other harness the remote is a cloud sandbox. For Concierge the natural remote is
/// a paired phone on the mesh — a device with power and signal doing work for one without.
/// The seam is the same either way, which is the point of having one.
/// </remarks>
public sealed class RemoteExecutionTests
{
    [Fact]
    public async Task A_local_provider_runs_here()
    {
        var provider = new LocalExecutionProvider();

        Assert.True(provider.IsLocal);
        Assert.Equal("this device", await provider.DescribeAsync());
    }

    [Fact]
    public async Task A_registered_provider_is_selected_by_name()
    {
        var registry = new ExecutionProviderRegistry();
        registry.Register("peer", new StubProvider("a phone on the mesh"));

        Assert.Equal("a phone on the mesh", await registry.Require("peer").DescribeAsync());
    }

    [Fact]
    public void An_unregistered_provider_is_refused_by_name()
    {
        var registry = new ExecutionProviderRegistry();

        var failure = Assert.Throws<InvalidOperationException>(() => registry.Require("peer"));
        Assert.Contains("peer", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_local_provider_is_always_available()
    {
        Assert.NotNull(new ExecutionProviderRegistry().Require("local"));
    }

    [Fact]
    public void Registered_providers_can_be_listed()
    {
        var registry = new ExecutionProviderRegistry();
        registry.Register("peer", new StubProvider("a peer"));

        Assert.Contains("peer", registry.Names);
        Assert.Contains("local", registry.Names);
    }

    [Fact]
    public void A_remote_provider_does_not_claim_to_be_local()
    {
        Assert.False(new StubProvider("elsewhere").IsLocal);
    }

    private sealed class StubProvider(string description) : IExecutionProvider
    {
        public bool IsLocal => false;

        public Task<string> DescribeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(description);
    }
}
