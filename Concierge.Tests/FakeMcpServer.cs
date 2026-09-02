using System.IO.Pipes;
using System.Text.Json.Nodes;
using Concierge.Shared.Rpc;
using Concierge.Shared.Rpc.Mcp;

namespace Concierge.Tests;

/// <summary>
/// An MCP server in the same process, so the protocol tests need no external binary, no
/// network and no ports.
/// </summary>
/// <remarks>
/// It records what it was asked, which is how the tests assert that arguments reach the
/// server unchanged rather than only that a call returned something.
/// </remarks>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _clientToServerWrite;
    private readonly AnonymousPipeClientStream _clientToServerRead;
    private readonly AnonymousPipeServerStream _serverToClientWrite;
    private readonly AnonymousPipeClientStream _serverToClientRead;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    public FakeMcpServer()
    {
        _clientToServerWrite = new AnonymousPipeServerStream(PipeDirection.Out);
        _clientToServerRead = new AnonymousPipeClientStream(
            PipeDirection.In,
            _clientToServerWrite.GetClientHandleAsString());

        _serverToClientWrite = new AnonymousPipeServerStream(PipeDirection.Out);
        _serverToClientRead = new AnonymousPipeClientStream(
            PipeDirection.In,
            _serverToClientWrite.GetClientHandleAsString());

        _loop = Task.Run(ServeAsync);
    }

    /// <summary>The tools this server publishes.</summary>
    public List<(string Name, string Description)> Tools { get; } = [];

    /// <summary>What the client sent on <c>initialize</c>.</summary>
    public JsonObject? LastInitializeParams { get; private set; }

    /// <summary>What the client sent on the most recent <c>tools/call</c>.</summary>
    public JsonObject? LastCallParams { get; private set; }

    /// <summary>Connect a client to this server.</summary>
    public Task<McpClient> ConnectAsync()
        => McpClient.ConnectAsync(_clientToServerWrite, _serverToClientRead);

    private async Task ServeAsync()
    {
        var framing = new LineFraming();

        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var raw = await framing.ReadAsync(_clientToServerRead, _shutdown.Token).ConfigureAwait(false);
                if (raw is null)
                {
                    break;
                }

                var request = JsonRpcMessage.Parse(raw);
                if (request?.Id is not { } id || request.Method is not { } method)
                {
                    continue;
                }

                var reply = Handle(id, method, request.Params);
                await framing.WriteAsync(_serverToClientWrite, reply.ToJson(), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (IOException)
        {
            // The pipe closed first.
        }
    }

    private JsonRpcMessage Handle(int id, string method, JsonObject? parameters)
    {
        switch (method)
        {
            case "initialize":
                LastInitializeParams = parameters;
                return JsonRpcMessage.Reply(id, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1.0" },
                });

            case "tools/list":
                var listed = new JsonArray();
                foreach (var (name, description) in Tools)
                {
                    listed.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["description"] = description,
                        ["inputSchema"] = new JsonObject { ["type"] = "object" },
                    });
                }

                return JsonRpcMessage.Reply(id, new JsonObject { ["tools"] = listed });

            case "tools/call":
                LastCallParams = parameters;
                var requested = parameters?["name"]?.GetValue<string>();
                if (!Tools.Any(tool => tool.Name == requested))
                {
                    return JsonRpcMessage.Error(id, -32602, $"Unknown tool '{requested}'.");
                }

                var query = parameters?["arguments"]?["query"]?.GetValue<string>() ?? string.Empty;
                return JsonRpcMessage.Reply(id, new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = $"results for {query}" },
                    },
                });

            default:
                return JsonRpcMessage.Error(id, -32601, $"No such method '{method}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        await _clientToServerWrite.DisposeAsync().ConfigureAwait(false);
        await _clientToServerRead.DisposeAsync().ConfigureAwait(false);
        await _serverToClientWrite.DisposeAsync().ConfigureAwait(false);
        await _serverToClientRead.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
