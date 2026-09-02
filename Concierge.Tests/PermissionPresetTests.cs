using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What a permission preset must do (parity feature 2): one named choice decides both which
/// tools a session may reach and whether it has to ask before acting.
/// </summary>
/// <remarks>
/// The point is that Kid Mode is a composition, not a filter — a session that cannot reach
/// the shell needs no rule telling it not to use one.
/// </remarks>
public sealed class PermissionPresetTests : IDisposable
{
    private readonly string _workspace;
    private readonly AgentHarnessService _harness;

    public PermissionPresetTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"concierge-preset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _harness = new AgentHarnessService(_workspace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory.
        }
    }

    // ── What each preset lets through ──────────────────────────────────

    [Fact]
    public void Read_only_offers_no_tool_that_can_change_anything()
    {
        var tools = ToolsFor(ConciergePermissionPreset.ReadOnly);

        Assert.All(tools, tool => Assert.True(tool.IsReadOnly));
    }

    [Fact]
    public void Read_only_still_offers_reading()
    {
        var tools = ToolsFor(ConciergePermissionPreset.ReadOnly);

        Assert.Contains(tools, tool => tool.Name == "read_file");
    }

    [Fact]
    public void Workspace_write_offers_writing()
    {
        var tools = ToolsFor(ConciergePermissionPreset.WorkspaceWrite);

        Assert.Contains(tools, tool => tool.Name == "write_file");
    }

    [Fact]
    public void Full_access_offers_running_commands()
    {
        var tools = ToolsFor(ConciergePermissionPreset.FullAccess);

        Assert.Contains(tools, tool => tool.Name == "run_command");
    }

    [Fact]
    public void Workspace_write_does_not_offer_running_commands()
    {
        var tools = ToolsFor(ConciergePermissionPreset.WorkspaceWrite);

        Assert.DoesNotContain(tools, tool => tool.Name == "run_command");
    }

    // ── A read-only session cannot act even if something calls the tool ─

    [Fact]
    public async Task A_read_only_session_leaves_the_file_unwritten_even_when_the_approver_says_yes()
    {
        var policy = ConciergePermissionPolicy.For(ConciergePermissionPreset.ReadOnly);
        var approval = policy.Wrap(new AlwaysAllow());
        var tool = new AgentHarnessWriteTool(_harness, approval);

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        Assert.False(File.Exists(Path.Combine(_workspace, "notes.txt")));
    }

    // ── Asking is per preset ───────────────────────────────────────────

    [Fact]
    public async Task Workspace_write_asks_before_writing()
    {
        var policy = ConciergePermissionPolicy.For(ConciergePermissionPreset.WorkspaceWrite);
        var inner = new CountingApprover();
        var tool = new AgentHarnessWriteTool(_harness, policy.Wrap(inner));

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        Assert.Equal(1, inner.Asked);
    }

    [Fact]
    public async Task Full_access_writes_without_asking()
    {
        var policy = ConciergePermissionPolicy.For(ConciergePermissionPreset.FullAccess);
        var inner = new CountingApprover();
        var tool = new AgentHarnessWriteTool(_harness, policy.Wrap(inner));

        await tool.InvokeAsync(WriteArgs("notes.txt", "hello"));

        Assert.Equal(0, inner.Asked);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_workspace, "notes.txt")));
    }

    // ── The default is the safe one ────────────────────────────────────

    [Fact]
    public void The_default_preset_cannot_run_commands()
    {
        var tools = ToolsFor(ConciergePermissionPolicy.Default.Preset);

        Assert.DoesNotContain(tools, tool => tool.Name == "run_command");
    }

    private IReadOnlyList<IAgentTool> ToolsFor(ConciergePermissionPreset preset)
        => ConciergePermissionPolicy.For(preset).SelectTools(AllTools());

    private IReadOnlyList<IAgentTool> AllTools() =>
    [
        new AgentHarnessReadTool(_harness),
        new AgentHarnessWriteTool(_harness, UnavailableToolApprovalService.Instance),
        new AgentHarnessRunTool(_harness, UnavailableToolApprovalService.Instance),
    ];

    private static JsonNode WriteArgs(string path, string content)
        => new JsonObject { ["path"] = path, ["content"] = content };

    private sealed class AlwaysAllow : IToolApprovalService
    {
        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ToolApprovalDecision.Allowed);
    }

    private sealed class CountingApprover : IToolApprovalService
    {
        public int Asked { get; private set; }

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Asked++;
            return ValueTask.FromResult(ToolApprovalDecision.Allowed);
        }
    }
}
