using Concierge.Shared.Caching;
using Concierge.Shared.Devices;
using Concierge.Shared.Tools;
using System.Text.Json.Nodes;

namespace Concierge.Tests;

public sealed class ServiceEfficiencyTests
{
    [Fact]
    public async Task Status_polling_preserves_catalogue_cache_but_setup_invalidates_it()
    {
        var cache = new AsyncSnapshotCache<string, string>(12, 4096, s => s.Length, _ => true);
        var loads = 0;
        Task<string> Load() { loads++; return Task.FromResult("catalogue"); }
        await cache.GetAsync("Capabilities", TimeSpan.FromMinutes(1), Load, default);
        foreach (var verb in new[] { "Hello", "Status", "AskStatus", "SetupProgress" })
        {
            if (CircleAiReadCachePolicy.InvalidatesReads(verb)) cache.Invalidate();
            await cache.GetAsync("Capabilities", TimeSpan.FromMinutes(1), Load, default);
        }
        Assert.Equal(1, loads);
        foreach (var verb in new[] { "SetupStart", "SetPreferences", "UnknownFutureWrite" })
            Assert.True(CircleAiReadCachePolicy.InvalidatesReads(verb));
        cache.Invalidate();
        await cache.GetAsync("Capabilities", TimeSpan.FromMinutes(1), Load, default);
        Assert.Equal(2, loads);
        Assert.Equal(TimeSpan.Zero, CircleAiReadCachePolicy.LifetimeFor("AskStatus"));
    }

    [Fact]
    public async Task Large_catalogue_is_discoverable_without_sending_descriptions_on_every_turn()
    {
        var operations = Enumerable.Range(0, 30).Select(i => (IAgentTool)new Operation(i)).ToArray();
        IReadOnlyList<IAgentTool> catalogue = [];
        var describe = new DescribeToolsTool(() => catalogue);
        var registry = new AgentToolRegistry(operations.Append(describe));
        catalogue = registry.Tools;
        var prompt = registry.BuildDiscoveryPromptAddendum();
        Assert.True(prompt.Length < 1500, $"Catalogue preamble grew to {prompt.Length} characters.");
        foreach (var tool in operations) Assert.Contains(tool.Name, prompt);
        Assert.Contains("[requires approval]", prompt);
        var result = await describe.InvokeAsync(JsonNode.Parse("{\"names\":[\"operation_29\"]}"));
        Assert.True(result.Success);
        Assert.Contains(operations[29].Description, result.Output);
        Assert.Contains("required", result.Output);
        Assert.All(operations.Cast<Operation>(), t => Assert.False(t.Invoked));
    }

    private sealed class Operation(int index) : IAgentTool
    {
        public string Name => $"operation_{index}";
        public string Description => new('x', 500);
        public bool IsReadOnly => false;
        public bool Invoked { get; private set; }
        public JsonNode ArgumentsSchema => JsonNode.Parse("{\"type\":\"object\",\"required\":[\"path\"]}")!;
        public Task<AgentToolResult> InvokeAsync(JsonNode? args, CancellationToken ct = default)
        { Invoked = true; return Task.FromResult(new AgentToolResult(true, "done")); }
    }
}
