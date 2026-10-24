using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Context;
using Concierge.Shared.Tools;
namespace Concierge.Tests;
public sealed class ProgressiveToolDiscoveryTests
{
    [Fact]
    public async Task Discovery_keeps_complete_schemas_available_without_executing_operations()
    {
        var operation = new LargeTool(); IReadOnlyList<IAgentTool> catalogue = [];
        var describe = new DescribeToolsTool(() => catalogue);
        var registry = new AgentToolRegistry([operation, describe]); catalogue = registry.Tools;
        var compact = registry.BuildDiscoveryPromptAddendum();
        Assert.Contains(operation.Name, compact); Assert.Contains("[requires approval]", compact);
        Assert.DoesNotContain(new string('x', 200), compact);
        var meter = new HeuristicTokenMeter();
        Assert.True(meter.EstimateTokens(compact) < meter.EstimateTokens(registry.BuildSystemPromptAddendum()));
        var result = await describe.InvokeAsync(JsonNode.Parse("{\"names\":[\"important_operation\"]}"));
        Assert.True(result.Success); Assert.Contains(new string('x', 200), result.Output); Assert.False(operation.Invoked);
    }
    [Theory]
    [InlineData("{}")][InlineData("{\"names\":[]}")][InlineData("{\"names\":[22]}")][InlineData("{\"names\":[\"unknown\"]}")][InlineData("1")]
    public async Task Invalid_requests_are_explained(string json)
        => Assert.False((await new DescribeToolsTool(() => []).InvokeAsync(JsonNode.Parse(json))).Success);
    [Fact]
    public void Snapshot_cursor_preserves_a_message_arriving_before_snapshot_recording()
    {
        var snapshot = new ConversationCompactionSnapshot(1, [new("system", "Summary"), new("user", "First")], 10, 0);
        var events = new ConversationEvent[] {
            new() { Seq = 0, Type = ConversationEventType.UserMessage, Data = "First" },
            new() { Seq = 1, Type = ConversationEventType.UserMessage, Data = "Arrived meanwhile" },
            new() { Seq = 2, Type = ConversationEventType.CompactionReplace, Data = System.Text.Json.JsonSerializer.Serialize(snapshot) } };
        var expected = new[] { "Summary", "First", "Arrived meanwhile" };
        Assert.Equal(expected, ConversationHistoryProjection.Derive(null, events).Turns.Select(t => t.Content));
        Assert.Equal(expected, new ConversationReplay(events).Steps().Last().HistorySoFar.Select(t => t.Content));
    }
    private sealed class LargeTool : IAgentTool
    {
        public string Name => "important_operation"; public string Description => "Perform the important operation";
        public JsonNode ArgumentsSchema => new JsonObject { ["type"] = "object", ["description"] = new string('x', 20000) };
        public bool IsReadOnly => false; public bool Invoked { get; private set; }
        public Task<AgentToolResult> InvokeAsync(JsonNode? args, CancellationToken ct = default) { Invoked = true; return Task.FromResult(new AgentToolResult(true, "done")); }
    }
}
