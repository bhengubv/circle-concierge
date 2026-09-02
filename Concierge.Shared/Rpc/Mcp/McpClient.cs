using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Rpc.Mcp;

/// <summary>One tool a Model Context Protocol server publishes.</summary>
/// <param name="Name">The name to call it by.</param>
/// <param name="Description">What it does, for the model to read.</param>
/// <param name="InputSchema">Its argument schema, when the server declares one.</param>
public sealed record McpTool(string Name, string Description, JsonNode? InputSchema);

/// <summary>
/// Talks to a Model Context Protocol server, so tools published by other software can be used
/// as though they were Concierge's own.
/// </summary>
/// <remarks>
/// Written against the protocol, not a library. MCP is JSON-RPC with three methods that
/// matter — <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c> — so owning it costs
/// little and means there is nothing here that can be withdrawn.
/// </remarks>
public sealed class McpClient : IAsyncDisposable
{
    /// <summary>The protocol revision this client speaks.</summary>
    private const string ProtocolVersion = "2024-11-05";

    private readonly JsonRpcClient _rpc;

    private McpClient(JsonRpcClient rpc) => _rpc = rpc;

    /// <summary>Connect over a pair of streams and complete the handshake.</summary>
    public static async Task<McpClient> ConnectAsync(
        Stream outbound,
        Stream inbound,
        CancellationToken cancellationToken = default)
    {
        var rpc = new JsonRpcClient(outbound, inbound, new LineFraming());
        var client = new McpClient(rpc);

        await rpc.InvokeAsync(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["clientInfo"] = new JsonObject { ["name"] = "Concierge", ["version"] = "1.0" },
            },
            cancellationToken).ConfigureAwait(false);

        return client;
    }

    /// <summary>What the server publishes.</summary>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _rpc.InvokeAsync("tools/list", null, cancellationToken).ConfigureAwait(false);
        if (result?["tools"] is not JsonArray tools)
        {
            return [];
        }

        return tools
            .OfType<JsonObject>()
            .Select(tool => new McpTool(
                tool["name"]?.GetValue<string>() ?? string.Empty,
                tool["description"]?.GetValue<string>() ?? string.Empty,
                tool["inputSchema"]?.DeepClone()))
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .ToList();
    }

    /// <summary>Run one of the server's tools.</summary>
    /// <remarks>
    /// A refusal comes back as a failed result rather than an exception, so a remote server
    /// having a bad day cannot end the conversation that called it.
    /// </remarks>
    public async Task<AgentToolResult> CallToolAsync(
        string name,
        JsonNode? arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _rpc.InvokeAsync(
                "tools/call",
                new JsonObject { ["name"] = name, ["arguments"] = arguments?.DeepClone() ?? new JsonObject() },
                cancellationToken).ConfigureAwait(false);

            return new AgentToolResult(true, ReadContent(result), null);
        }
        catch (JsonRpcException failure)
        {
            return new AgentToolResult(false, string.Empty, failure.Message);
        }
        catch (TimeoutException)
        {
            return new AgentToolResult(false, string.Empty, $"The MCP server did not answer the call to '{name}'.");
        }
    }

    /// <summary>
    /// The server's tools, adapted so they can join <see cref="AgentToolRegistry"/> beside
    /// local ones.
    /// </summary>
    public async Task<IReadOnlyList<IAgentTool>> GetAgentToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await ListToolsAsync(cancellationToken).ConfigureAwait(false);
        return tools.Select(tool => (IAgentTool)new McpAgentTool(this, tool)).ToList();
    }

    /// <summary>
    /// Reads the text out of an MCP result. Content is a list of typed parts; the text parts
    /// are what a model can read, and anything else is ignored rather than rendered as noise.
    /// </summary>
    private static string ReadContent(JsonObject? result)
    {
        if (result?["content"] is not JsonArray content)
        {
            return result?.ToJsonString() ?? string.Empty;
        }

        var text = content
            .OfType<JsonObject>()
            .Where(part => part["type"]?.GetValue<string>() == "text")
            .Select(part => part["text"]?.GetValue<string>() ?? string.Empty);

        return string.Join('\n', text);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _rpc.DisposeAsync();
}

/// <summary>
/// One remote MCP tool wearing the local tool interface.
/// </summary>
/// <remarks>
/// Reported as not read-only, deliberately. The protocol says nothing about whether a tool
/// changes state or is safe to overlap with others, and assuming the careful answer is the
/// only sound default for code written by someone else.
/// </remarks>
internal sealed class McpAgentTool(McpClient client, McpTool tool) : IAgentTool
{
    public string Name => tool.Name;

    public string Description => tool.Description;

    public JsonNode? ArgumentsSchema => tool.InputSchema;

    public bool IsReadOnly => false;

    public Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
        => client.CallToolAsync(tool.Name, arguments, cancellationToken);
}
