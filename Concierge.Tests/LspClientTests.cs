using System.IO.Pipes;
using System.Text.Json.Nodes;
using Concierge.Shared.Rpc;
using Concierge.Shared.Rpc.Lsp;

namespace Concierge.Tests;

/// <summary>
/// What the LSP client must do (parity feature 42): answer where something is defined and
/// where it is used, from a real language server rather than from the model guessing.
/// </summary>
/// <remarks>
/// Bounded on purpose to definitions, references and diagnostics. A model that cannot hold a
/// codebase in context needs somewhere authoritative to ask; it does not need a full editor.
/// </remarks>
public sealed class LspClientTests
{
    [Fact]
    public async Task Connecting_tells_the_server_which_workspace_to_index()
    {
        await using var server = new FakeLspServer();
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        Assert.Contains("rootUri", server.LastInitializeParams!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connecting_sends_the_initialized_notification()
    {
        // A server that never receives it may refuse everything that follows.
        await using var server = new FakeLspServer();
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        Assert.True(await server.WaitForInitializedAsync());
    }

    [Fact]
    public async Task A_definition_comes_back_with_its_file_and_line()
    {
        await using var server = new FakeLspServer();
        server.DefinitionLine = 41;
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        var found = Assert.Single(await client.GoToDefinitionAsync("Program.cs", 10, 4));

        Assert.Equal(41, found.Line);
        Assert.EndsWith("Program.cs", found.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task References_come_back_as_a_list()
    {
        await using var server = new FakeLspServer();
        server.ReferenceCount = 3;
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        Assert.Equal(3, (await client.FindReferencesAsync("Program.cs", 10, 4)).Count);
    }

    [Fact]
    public async Task Asking_for_references_includes_the_declaration()
    {
        await using var server = new FakeLspServer();
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        await client.FindReferencesAsync("Program.cs", 10, 4);

        Assert.Contains("includeDeclaration", server.LastRequestParams!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_come_back_with_their_message()
    {
        await using var server = new FakeLspServer();
        server.DiagnosticMessage = "CS0103: the name does not exist";
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        var reported = Assert.Single(await client.GetDiagnosticsAsync("Program.cs"));

        Assert.Contains("CS0103", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_refuses_a_request_gives_an_empty_answer()
    {
        // Servers differ in what they implement. A refusal means "nothing found", not a
        // crash in the middle of a model's turn.
        await using var server = new FakeLspServer { RefuseEverything = true };
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        Assert.Empty(await client.GoToDefinitionAsync("Program.cs", 1, 1));
    }

    [Fact]
    public async Task A_server_that_does_not_do_diagnostics_gives_an_empty_answer()
    {
        await using var server = new FakeLspServer { RefuseEverything = true };
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        Assert.Empty(await client.GetDiagnosticsAsync("Program.cs"));
    }

    [Fact]
    public async Task A_file_with_nothing_wrong_reports_nothing()
    {
        await using var server = new FakeLspServer();
        await using var client = await server.ConnectAsync(Path.GetTempPath());

        Assert.Empty(await client.GetDiagnosticsAsync("Program.cs"));
    }
}

/// <summary>
/// A language server in the same process, framed the way LSP frames things.
/// </summary>
internal sealed class FakeLspServer : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _clientToServerWrite;
    private readonly AnonymousPipeClientStream _clientToServerRead;
    private readonly AnonymousPipeServerStream _serverToClientWrite;
    private readonly AnonymousPipeClientStream _serverToClientRead;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _loop;

    public FakeLspServer()
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

    public int DefinitionLine { get; set; } = 0;

    public int ReferenceCount { get; set; } = 1;

    public string? DiagnosticMessage { get; set; }

    public bool RefuseEverything { get; set; }

    public JsonObject? LastInitializeParams { get; private set; }

    public JsonObject? LastRequestParams { get; private set; }

    public Task<bool> ConnectedTask => _initialized.Task;

    public Task<LspClient> ConnectAsync(string rootPath)
        => LspClient.ConnectAsync(_clientToServerWrite, _serverToClientRead, rootPath);

    public async Task<bool> WaitForInitializedAsync()
    {
        try
        {
            return await _initialized.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task ServeAsync()
    {
        var framing = new HeaderFraming();

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
                if (request?.Method is not { } method)
                {
                    continue;
                }

                if (request.Id is not { } id)
                {
                    if (method == "initialized")
                    {
                        _initialized.TrySetResult(true);
                    }

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
        if (method == "initialize")
        {
            LastInitializeParams = parameters;
            return JsonRpcMessage.Reply(id, new JsonObject { ["capabilities"] = new JsonObject() });
        }

        LastRequestParams = parameters;

        if (RefuseEverything)
        {
            return JsonRpcMessage.Error(id, -32601, $"'{method}' is not supported.");
        }

        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>() ?? string.Empty;

        return method switch
        {
            "textDocument/definition" => JsonRpcMessage.Reply(id, new JsonObject
            {
                ["locations"] = new JsonArray { Location(uri, DefinitionLine) },
            }),

            "textDocument/references" => JsonRpcMessage.Reply(id, new JsonObject
            {
                ["locations"] = new JsonArray(
                    Enumerable.Range(0, ReferenceCount).Select(i => Location(uri, i)).ToArray()),
            }),

            "textDocument/diagnostic" => JsonRpcMessage.Reply(id, new JsonObject
            {
                ["items"] = DiagnosticMessage is null
                    ? new JsonArray()
                    : new JsonArray
                    {
                        new JsonObject
                        {
                            ["message"] = DiagnosticMessage,
                            ["severity"] = 1,
                            ["range"] = new JsonObject
                            {
                                ["start"] = new JsonObject { ["line"] = 3, ["character"] = 0 },
                            },
                        },
                    },
            }),

            _ => JsonRpcMessage.Error(id, -32601, $"No such method '{method}'."),
        };
    }

    private static JsonNode Location(string uri, int line) => new JsonObject
    {
        ["uri"] = uri,
        ["range"] = new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = line, ["character"] = 0 },
        },
    };

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
