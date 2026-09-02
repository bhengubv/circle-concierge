using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Headless;
using Concierge.Shared.Rpc;
using Concierge.Shared.Rpc.Acp;
using Concierge.Shared.Rpc.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// What the RPC server must do (parity feature 33): let something outside Concierge call in,
/// and survive whatever it sends.
/// </summary>
public sealed class JsonRpcServerTests
{
    [Fact]
    public async Task A_registered_method_is_answered()
    {
        var server = new JsonRpcServer().Handle("ping", (_, _) =>
            Task.FromResult(new JsonObject { ["pong"] = true }));

        var reply = await server.HandleAsync(JsonRpcMessage.Request(1, "ping", null));

        Assert.True(reply!.Result!["pong"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_unknown_method_is_refused_politely()
    {
        var server = new JsonRpcServer();

        var reply = await server.HandleAsync(JsonRpcMessage.Request(1, "nope", null));

        Assert.True(reply!.IsError);
        Assert.Equal(-32601, reply.ErrorCode);
    }

    [Fact]
    public async Task A_handler_that_throws_becomes_an_error_reply()
    {
        // The peer is another program. A bad request must not end the connection serving it.
        var server = new JsonRpcServer().Handle("break", (_, _) =>
            throw new InvalidOperationException("it broke"));

        var reply = await server.HandleAsync(JsonRpcMessage.Request(1, "break", null));

        Assert.True(reply!.IsError);
        Assert.Contains("it broke", reply.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_notification_is_answered_with_silence()
    {
        var server = new JsonRpcServer().Handle("note", (_, _) => Task.FromResult(new JsonObject()));

        Assert.Null(await server.HandleAsync(JsonRpcMessage.Notification("note", null)));
    }

    [Fact]
    public async Task A_notification_that_throws_is_still_answered_with_silence()
    {
        var server = new JsonRpcServer().Handle("note", (_, _) => throw new InvalidOperationException("x"));

        Assert.Null(await server.HandleAsync(JsonRpcMessage.Notification("note", null)));
    }

    [Fact]
    public async Task Parameters_reach_the_handler()
    {
        JsonObject? seen = null;
        var server = new JsonRpcServer().Handle("echo", (parameters, _) =>
        {
            seen = parameters;
            return Task.FromResult(new JsonObject());
        });

        await server.HandleAsync(JsonRpcMessage.Request(1, "echo", new JsonObject { ["value"] = "sent" }));

        Assert.Equal("sent", seen!["value"]!.GetValue<string>());
    }

    [Fact]
    public void Registered_methods_can_be_listed()
    {
        var server = new JsonRpcServer()
            .Handle("one", (_, _) => Task.FromResult(new JsonObject()))
            .Handle("two", (_, _) => Task.FromResult(new JsonObject()));

        Assert.Equal(["one", "two"], server.Methods.Order());
    }
}

/// <summary>
/// What the ACP server must do (parity feature 31): let another program hold a conversation
/// with Concierge over the wire.
/// </summary>
/// <remarks>
/// This is also what lets the other Geek apps use Concierge as their assistant layer — they
/// drive a session rather than embedding a runtime.
/// </remarks>
public sealed class AcpServerTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"concierge-acp-{Guid.NewGuid():N}.db");

    private ServiceProvider _provider = null!;
    private IConversationStore _store = null!;
    private JsonRpcServer _server = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection().AddConciergeChat(_databasePath).BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = _provider.GetRequiredService<IConversationStore>();
        _server = new AcpServer(_store, new HeadlessRunner(new ScriptedRuntime("the answer"))).Build();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Disposable temp file.
        }
    }

    [Fact]
    public async Task Initialising_reports_the_agent()
    {
        var reply = await _server.HandleAsync(JsonRpcMessage.Request(1, "initialize", null));

        Assert.Equal("Concierge", reply!.Result!["agentInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_new_session_can_be_started()
    {
        var reply = await _server.HandleAsync(JsonRpcMessage.Request(1, "session/new", null));

        Assert.True(Guid.TryParse(reply!.Result!["sessionId"]!.GetValue<string>(), out _));
    }

    [Fact]
    public async Task A_prompt_is_answered()
    {
        var sessionId = await NewSessionAsync();

        var reply = await _server.HandleAsync(JsonRpcMessage.Request(2, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = "a question",
        }));

        Assert.Equal("the answer", reply!.Result!["output"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_prompt_and_its_answer_are_recorded_in_the_log()
    {
        var sessionId = await NewSessionAsync();

        await _server.HandleAsync(JsonRpcMessage.Request(2, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = "a question",
        }));

        var events = await _store.ReadEventsAsync(Guid.Parse(sessionId));
        Assert.Equal(
            [ConversationEventType.UserMessage, ConversationEventType.AssistantMessage],
            events.Select(e => e.Type));
    }

    [Fact]
    public async Task History_comes_back_over_the_wire()
    {
        var sessionId = await NewSessionAsync();
        await _server.HandleAsync(JsonRpcMessage.Request(2, "session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = "a question",
        }));

        var reply = await _server.HandleAsync(JsonRpcMessage.Request(3, "session/history", new JsonObject
        {
            ["sessionId"] = sessionId,
        }));

        Assert.Equal(2, (reply!.Result!["turns"] as JsonArray)!.Count);
    }

    [Fact]
    public async Task A_prompt_without_a_session_is_refused()
    {
        var reply = await _server.HandleAsync(JsonRpcMessage.Request(1, "session/prompt", new JsonObject
        {
            ["prompt"] = "a question",
        }));

        Assert.True(reply!.IsError);
    }

    [Fact]
    public async Task A_prompt_for_a_session_that_does_not_exist_is_refused()
    {
        var reply = await _server.HandleAsync(JsonRpcMessage.Request(1, "session/prompt", new JsonObject
        {
            ["sessionId"] = Guid.NewGuid().ToString(),
            ["prompt"] = "a question",
        }));

        Assert.True(reply!.IsError);
    }

    [Fact]
    public async Task The_whole_exchange_works_over_a_pipe()
    {
        // End to end: a real client, real framing, no shortcuts through HandleAsync.
        using var clientToServerWrite = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientToServerRead = new AnonymousPipeClientStream(
            PipeDirection.In, clientToServerWrite.GetClientHandleAsString());
        using var serverToClientWrite = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverToClientRead = new AnonymousPipeClientStream(
            PipeDirection.In, serverToClientWrite.GetClientHandleAsString());

        using var shutdown = new CancellationTokenSource();
        var serving = Task.Run(() => _server.ServeAsync(clientToServerRead, serverToClientWrite, shutdown.Token));

        await using var client = new JsonRpcClient(clientToServerWrite, serverToClientRead, new LineFraming());
        var result = await client.InvokeAsync("initialize", null);

        Assert.Equal("Concierge", result!["agentInfo"]!["name"]!.GetValue<string>());
        await shutdown.CancelAsync();
    }

    private async Task<string> NewSessionAsync()
    {
        var reply = await _server.HandleAsync(JsonRpcMessage.Request(1, "session/new", null));
        return reply!.Result!["sessionId"]!.GetValue<string>();
    }

    private sealed class ScriptedRuntime(string answer) : IChatRuntime
    {
        public string Id => "scripted";
        public string EngineLabel => "Scripted";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return answer;
        }
    }
}
