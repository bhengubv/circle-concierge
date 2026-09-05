using System.Text.Json.Nodes;
using Concierge.Shared.Rpc.Mcp;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Tools published by other software.
///
/// The client was already complete — handshake, tools/list, tools/call, and an
/// IAgentTool adapter — and was never instantiated outside its own file. What
/// was missing was everything around it: configuration, connection, a way into
/// the registry, and a person being asked before somebody else's code runs on
/// this machine.
///
/// These tests cover the parts that can be checked without a real server: the
/// config file, the registry's willingness to take late-arriving tools, and the
/// two properties that matter for safety — namespacing and approval.
/// </summary>
public sealed class McpTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"mcp-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // Swept with the test-run temp root regardless.
        }
    }

    // ── Configuration ─────────────────────────────────────────────────────

    /// <summary>
    /// No file means no servers, and no complaint. An assistant that shipped a
    /// sample config and quietly started talking to other software would be a
    /// poor trade for saving somebody one paste.
    /// </summary>
    [Fact]
    public void With_no_file_nothing_is_configured_and_nothing_is_wrong()
    {
        var (servers, problem) = new McpServerOptions(_path).Load();

        Assert.Empty(servers);
        Assert.Null(problem);
    }

    [Fact]
    public void A_configured_server_is_read()
    {
        File.WriteAllText(_path, """
            [
              { "name": "files", "command": "npx", "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "."] }
            ]
            """);

        var (servers, problem) = new McpServerOptions(_path).Load();

        Assert.Null(problem);
        var server = Assert.Single(servers);
        Assert.Equal("files", server.Name);
        Assert.Equal("npx", server.Command);
        Assert.Equal(3, server.Arguments!.Count);
        Assert.True(server.Enabled);
    }

    /// <summary>
    /// Configured but not started, so a server can stay on file without being
    /// granted a way into the conversation.
    /// </summary>
    [Fact]
    public void A_server_can_be_turned_off_without_being_removed()
    {
        File.WriteAllText(_path, """
            [ { "name": "files", "command": "npx", "enabled": false } ]
            """);

        var (servers, _) = new McpServerOptions(_path).Load();

        Assert.False(Assert.Single(servers).Enabled);
    }

    /// <summary>
    /// A person who has just edited this file needs to know it did not take.
    /// Silence would look exactly like a server that failed to start.
    /// </summary>
    [Fact]
    public void A_broken_file_says_so_rather_than_failing_quietly()
    {
        File.WriteAllText(_path, "{ this is not json");

        var (servers, problem) = new McpServerOptions(_path).Load();

        Assert.Empty(servers);
        Assert.NotNull(problem);
        Assert.Contains("not valid JSON", problem!);
    }

    [Fact]
    public void An_entry_missing_a_command_is_skipped_and_counted()
    {
        File.WriteAllText(_path, """
            [
              { "name": "good", "command": "npx" },
              { "name": "no-command" }
            ]
            """);

        var (servers, problem) = new McpServerOptions(_path).Load();

        Assert.Single(servers);
        Assert.Contains("skipped", problem!);
    }

    // ── The registry ──────────────────────────────────────────────────────

    /// <summary>
    /// The bug that made MCP unreachable even once connected: the registry
    /// snapshotted its tools at construction, and a server's tools are not
    /// known until it has been started and asked — which happens after.
    /// </summary>
    [Fact]
    public void The_registry_sees_tools_that_arrive_after_it_is_built()
    {
        var source = new MutableSource();
        var registry = new AgentToolRegistry([new LocalTool("read_file")], [source]);

        Assert.Single(registry.Tools);

        source.Tools = [new LocalTool("remote_thing")];

        Assert.Equal(2, registry.Tools.Count);
        Assert.Contains(registry.Tools, t => t.Name == "remote_thing");
    }

    /// <summary>
    /// A server must not be able to shadow a local tool by publishing one with
    /// the same name — read_file has to stay read_file.
    /// </summary>
    [Fact]
    public void A_remote_tool_cannot_shadow_a_local_one()
    {
        var source = new MutableSource { Tools = [new LocalTool("read_file", "remote")] };
        var registry = new AgentToolRegistry([new LocalTool("read_file", "local")], [source]);

        var tool = Assert.Single(registry.Tools);
        Assert.Equal("local", tool.Description);
    }

    [Fact]
    public void The_registry_still_works_with_no_sources_at_all()
    {
        var registry = new AgentToolRegistry([new LocalTool("read_file")]);

        Assert.Single(registry.Tools);
    }

    // ── Safety ────────────────────────────────────────────────────────────

    /// <summary>
    /// Namespaced by server, because two servers may both publish "search" and
    /// a collision silently resolved by first-wins is a call going somewhere
    /// nobody chose.
    /// </summary>
    [Fact]
    public void A_remote_tool_carries_the_name_of_the_server_it_came_from()
    {
        var tool = Wrap("My Files", new LocalTool("search"), new FixedApproval(ToolApprovalDecision.Allowed));

        Assert.Equal("my_files__search", tool.Name);
        Assert.Contains("My Files", tool.Description);
    }

    /// <summary>
    /// The property that matters most here. Local tools that can act ask for
    /// themselves; a remote tool arriving through a protocol that says nothing
    /// about what it does would otherwise be the one way to make something
    /// happen on this machine without being asked.
    /// </summary>
    [Fact]
    public async Task A_remote_tool_asks_before_it_runs()
    {
        var inner = new LocalTool("search");
        var tool = Wrap("files", inner, new FixedApproval(ToolApprovalDecision.Denied));

        var result = await tool.InvokeAsync(null);

        Assert.False(result.Success);
        Assert.False(inner.WasInvoked);
    }

    [Fact]
    public async Task A_remote_tool_runs_once_allowed()
    {
        var inner = new LocalTool("search");
        var tool = Wrap("files", inner, new FixedApproval(ToolApprovalDecision.Allowed));

        var result = await tool.InvokeAsync(null);

        Assert.True(result.Success);
        Assert.True(inner.WasInvoked);
    }

    /// <summary>
    /// With no approver wired the default answers Unavailable, and a remote
    /// tool must treat that as a refusal like every other acting tool does.
    /// </summary>
    [Fact]
    public async Task A_remote_tool_refuses_when_nobody_can_be_asked()
    {
        var inner = new LocalTool("search");
        var tool = Wrap("files", inner, UnavailableToolApprovalService.Instance);

        Assert.False((await tool.InvokeAsync(null)).Success);
        Assert.False(inner.WasInvoked);
    }

    [Fact]
    public void A_remote_tool_never_claims_to_be_read_only()
        => Assert.False(Wrap("files", new LocalTool("search"),
            UnavailableToolApprovalService.Instance).IsReadOnly);

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reaches the internal wrapper the source builds. Internal because it is
    /// implementation, and tested because it carries the safety property.
    /// </summary>
    private static IAgentTool Wrap(string server, IAgentTool inner, IToolApprovalService approval)
        => (IAgentTool)Activator.CreateInstance(
            typeof(McpToolSource).Assembly.GetType("Concierge.Shared.Rpc.Mcp.ApprovedMcpTool")!,
            server, inner, approval)!;

    private sealed class MutableSource : IAgentToolSource
    {
        public IReadOnlyList<IAgentTool> Tools { get; set; } = [];
    }

    private sealed class LocalTool : IAgentTool
    {
        public LocalTool(string name, string description = "does a thing")
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string Description { get; }
        public JsonNode? ArgumentsSchema => null;
        public bool IsReadOnly => true;
        public bool WasInvoked { get; private set; }

        public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            return Task.FromResult(new AgentToolResult(true, "done", null));
        }
    }

    private sealed class FixedApproval : IToolApprovalService
    {
        private readonly ToolApprovalDecision _decision;

        public FixedApproval(ToolApprovalDecision decision) => _decision = decision;

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_decision);
    }
}
