using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void Add_concierge_tools_registers_three_harness_adapters_in_the_catalogue()
    {
        var workspace = CreateWorkspaceRoot();
        var harness = new AgentHarnessService(workspace);

        using var provider = new ServiceCollection()
            .AddSingleton<IAgentHarnessService>(harness)
            .AddConciergeTools()
            .BuildServiceProvider();

        var registry = provider.GetRequiredService<IAgentToolRegistry>();

        Assert.Equal(3, registry.Tools.Count);
        Assert.Contains(registry.Tools, t => t.Name == "read_file" && t.IsReadOnly);
        Assert.Contains(registry.Tools, t => t.Name == "write_file" && !t.IsReadOnly);
        Assert.Contains(registry.Tools, t => t.Name == "run_command" && !t.IsReadOnly);
    }

    [Fact]
    public void Tool_registry_renders_a_system_prompt_addendum_listing_every_tool()
    {
        var workspace = CreateWorkspaceRoot();
        var harness = new AgentHarnessService(workspace);
        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            new AgentHarnessReadTool(harness),
            new AgentHarnessWriteTool(harness),
            new AgentHarnessRunTool(harness),
        });

        var addendum = registry.BuildSystemPromptAddendum();

        Assert.Contains("```tool-call", addendum, StringComparison.Ordinal);
        Assert.Contains("read_file", addendum, StringComparison.Ordinal);
        Assert.Contains("write_file", addendum, StringComparison.Ordinal);
        Assert.Contains("run_command", addendum, StringComparison.Ordinal);
        Assert.Contains("[read-only]", addendum, StringComparison.Ordinal);
        Assert.Contains("[requires approval]", addendum, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_tool_returns_file_contents_through_the_harness_sandbox()
    {
        var workspace = CreateWorkspaceRoot();
        var fileRel = $"sample-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(workspace, fileRel), "hello from tool test");
        var harness = new AgentHarnessService(workspace);
        var tool = new AgentHarnessReadTool(harness);

        var arguments = JsonNode.Parse($"{{ \"path\": \"{fileRel}\" }}");
        var result = await tool.InvokeAsync(arguments);

        Assert.True(result.Success);
        Assert.Contains("hello from tool test", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_tool_rejects_missing_path_argument()
    {
        var harness = new AgentHarnessService(CreateWorkspaceRoot());
        var tool = new AgentHarnessReadTool(harness);

        var result = await tool.InvokeAsync(JsonNode.Parse("{}"));

        Assert.False(result.Success);
        Assert.Contains("path", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_tool_requires_approval_so_unapproved_call_returns_approval_required()
    {
        var workspace = CreateWorkspaceRoot();
        var harness = new AgentHarnessService(workspace);
        var tool = new AgentHarnessWriteTool(harness);

        var arguments = JsonNode.Parse("{ \"path\": \"out.txt\", \"content\": \"sample\" }");
        var result = await tool.InvokeAsync(arguments);

        Assert.False(result.Success);
        Assert.Contains("approval", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateWorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Concierge.slnx"), string.Empty);
        return root;
    }
}
