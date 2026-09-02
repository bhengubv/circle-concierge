using System.Text.Json.Nodes;
using Concierge.Shared.Rpc;
using Concierge.Shared.Rpc.Mcp;

namespace Concierge.Tests;

/// <summary>
/// What the MCP client must do (parity feature 44): use tools published by other software as
/// though they were Concierge's own.
/// </summary>
/// <remarks>
/// Written against the protocol rather than a library, so there is nothing to be cut off
/// from. A remote tool arrives as an <c>IAgentTool</c> and joins the registry, which means
/// the rest of the product needs no knowledge that MCP exists.
/// </remarks>
public sealed class McpClientTests
{
    [Fact]
    public async Task Connecting_announces_the_protocol_version()
    {
        await using var server = new FakeMcpServer();
        await using var client = await server.ConnectAsync();

        Assert.Contains("protocolVersion", server.LastInitializeParams!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_s_tools_are_listed()
    {
        await using var server = new FakeMcpServer();
        server.Tools.Add(("search_docs", "Search the documentation"));
        await using var client = await server.ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal("search_docs", Assert.Single(tools).Name);
    }

    [Fact]
    public async Task A_listed_tool_keeps_its_description()
    {
        await using var server = new FakeMcpServer();
        server.Tools.Add(("search_docs", "Search the documentation"));
        await using var client = await server.ConnectAsync();

        var tool = Assert.Single(await client.ListToolsAsync());

        Assert.Equal("Search the documentation", tool.Description);
    }

    [Fact]
    public async Task A_server_with_no_tools_lists_none()
    {
        await using var server = new FakeMcpServer();
        await using var client = await server.ConnectAsync();

        Assert.Empty(await client.ListToolsAsync());
    }

    [Fact]
    public async Task Calling_a_tool_returns_what_the_server_produced()
    {
        await using var server = new FakeMcpServer();
        server.Tools.Add(("search_docs", "Search"));
        await using var client = await server.ConnectAsync();

        var result = await client.CallToolAsync("search_docs", new JsonObject { ["query"] = "bell" });

        Assert.True(result.Success);
        Assert.Contains("bell", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_arguments_reach_the_server_unchanged()
    {
        await using var server = new FakeMcpServer();
        server.Tools.Add(("search_docs", "Search"));
        await using var client = await server.ConnectAsync();

        await client.CallToolAsync("search_docs", new JsonObject { ["query"] = "THE-EXACT-QUERY" });

        Assert.Contains("THE-EXACT-QUERY", server.LastCallParams!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_the_server_refuses_is_a_failed_result_not_an_exception()
    {
        await using var server = new FakeMcpServer();
        await using var client = await server.ConnectAsync();

        var result = await client.CallToolAsync("not_there", new JsonObject());

        Assert.False(result.Success);
    }

    [Fact]
    public async Task A_server_error_is_reported_in_words()
    {
        await using var server = new FakeMcpServer();
        await using var client = await server.ConnectAsync();

        var result = await client.CallToolAsync("not_there", new JsonObject());

        Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    // ── Joining the registry ───────────────────────────────────────────

    [Fact]
    public async Task A_remote_tool_can_be_used_as_a_local_one()
    {
        await using var server = new FakeMcpServer();
        server.Tools.Add(("search_docs", "Search the documentation"));
        await using var client = await server.ConnectAsync();

        var adapted = Assert.Single(await client.GetAgentToolsAsync());

        Assert.Equal("search_docs", adapted.Name);
    }

    [Fact]
    public async Task A_remote_tool_runs_through_the_local_interface()
    {
        await using var server = new FakeMcpServer();
        server.Tools.Add(("search_docs", "Search"));
        await using var client = await server.ConnectAsync();
        var adapted = Assert.Single(await client.GetAgentToolsAsync());

        var result = await adapted.InvokeAsync(new JsonObject { ["query"] = "bell" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task A_remote_tool_is_treated_as_able_to_change_things()
    {
        // Nothing in the protocol says whether a remote tool is safe to run unattended or to
        // overlap with others. Assuming the careful answer is the only sound default.
        await using var server = new FakeMcpServer();
        server.Tools.Add(("search_docs", "Search"));
        await using var client = await server.ConnectAsync();

        Assert.False(Assert.Single(await client.GetAgentToolsAsync()).IsReadOnly);
    }
}
