using System.Text.Json;
using Concierge.Shared.Rpc.Mcp;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

public sealed class PhoneUseMcpIntegrationTests
{
    [Fact]
    public async Task Installed_phoneuse_is_published_as_Concierge_tools()
    {
        var command = Environment.GetEnvironmentVariable("CONCIERGE_TEST_PHONEUSE_MCP");
        if (string.IsNullOrWhiteSpace(command))
            return; // Run against the installed tool when the path is supplied.

        Assert.True(File.Exists(command), $"phoneuse MCP executable missing: {command}");
        var configPath = Path.Combine(Path.GetTempPath(), $"concierge-phoneuse-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(configPath, JsonSerializer.Serialize(new[]
            {
                new McpServerConfig("phoneuse", command),
            }));
            await using var source = new McpToolSource(
                new McpServerOptions(configPath),
                new AllowForThisTest(),
                availableMemoryProvider: () => long.MaxValue);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await source.ConnectAllAsync(timeout.Token);

            var status = Assert.Single(source.Statuses);
            Assert.True(status.Connected, status.Status);
            Assert.Contains(source.Tools, tool => tool.Name == "phoneuse__status");
            Assert.Contains(source.Tools, tool => tool.Name == "phoneuse__look");
            Assert.Contains(source.Tools, tool => tool.Name == "phoneuse__tap");
            Assert.All(source.Tools, tool => Assert.False(tool.IsReadOnly));

            var statusTool = Assert.Single(source.Tools, tool => tool.Name == "phoneuse__status");
            var result = await statusTool.InvokeAsync(null, timeout.Token);
            Assert.True(result.Success, result.FailureMessage);
            Assert.False(string.IsNullOrWhiteSpace(result.Output));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private sealed class AllowForThisTest : IToolApprovalService
    {
        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(request.ToolName == "phoneuse__status"
                ? ToolApprovalDecision.Allowed
                : ToolApprovalDecision.Denied);
    }
}
