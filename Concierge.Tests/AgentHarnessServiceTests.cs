using Concierge.Shared;

namespace Concierge.Tests;

public sealed class AgentHarnessServiceTests
{
    [Fact]
    public void Tool_registry_exposes_core_claude_code_style_tools()
    {
        var harness = CreateHarness();

        var tools = harness.GetTools();

        Assert.Contains(tools, tool => tool.Name == "read_file" && tool.IsReadOnly);
        Assert.Contains(tools, tool => tool.Name == "write_file" && tool.IsDestructive && tool.Risk == ConciergeToolRisk.High);
        Assert.Contains(tools, tool => tool.Name == "shell" && tool.Risk == ConciergeToolRisk.High);
        Assert.Contains(tools, tool => tool.Name == "grep" && tool.IsConcurrencySafe);
    }

    [Fact]
    public async Task File_write_requires_approval_before_touching_workspace()
    {
        var harness = CreateHarness();
        var path = $".concierge-artifacts/tests/write-{Guid.NewGuid():N}.txt";

        var blocked = await harness.WriteFileAsync(path, "hello", approved: false);
        var written = await harness.WriteFileAsync(path, "hello", approved: true);
        var read = await harness.ReadFileAsync(path);

        Assert.Equal(ConciergeToolOutcome.ApprovalRequired, blocked.Outcome);
        Assert.Equal(ConciergeToolOutcome.Succeeded, written.Outcome);
        Assert.Equal("hello", read.Output);
    }

    [Theory]
    [InlineData("dotnet test && dotnet build")]
    [InlineData("OPENAI_API_KEY=secret dotnet test")]
    [InlineData("C:\\Windows\\System32\\cmd.exe /c dir")]
    public async Task Shell_blocks_unsafe_command_shapes(string command)
    {
        var harness = CreateHarness();

        var result = await harness.RunCommandAsync(command, approved: true);

        Assert.Equal(ConciergeToolOutcome.Denied, result.Outcome);
    }

    [Fact]
    public async Task Read_only_commands_can_run_without_approval()
    {
        var harness = CreateHarness();

        var result = await harness.RunCommandAsync("dotnet --info", approved: false);

        Assert.Equal(ConciergeToolOutcome.Succeeded, result.Outcome);
        Assert.Contains(".NET", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_goal_persists_run_log()
    {
        var harness = CreateHarness();

        var log = await harness.RunGoalAsync("Inspect runtime", ["dotnet --info"], approved: false);
        var logs = harness.GetRunLogs();

        Assert.NotEmpty(log.Results);
        Assert.Contains(logs, item => item.Id == log.Id);
    }

    private static AgentHarnessService CreateHarness()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-harness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Concierge.slnx"), string.Empty);
        return new AgentHarnessService(root);
    }
}
