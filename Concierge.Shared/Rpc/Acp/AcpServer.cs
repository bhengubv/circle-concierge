using System.Text.Json.Nodes;
using Concierge.Shared.Chat;
using Concierge.Shared.Headless;
using Concierge.Shared.Rpc.Server;

namespace Concierge.Shared.Rpc.Acp;

/// <summary>
/// Lets another program drive a Concierge session over JSON-RPC.
/// </summary>
/// <remarks>
/// <para>
/// The methods here are the Agent Client Protocol's shape: initialise, start a session, send a
/// prompt, read it back. They are registered on the ordinary
/// <see cref="JsonRpcServer" />, so this file is a vocabulary rather than an implementation.
/// </para>
/// <para>
/// This is also what makes Concierge usable as the assistant layer for the other Geek apps
/// rather than only as its own product — they drive a session instead of embedding a runtime.
/// </para>
/// </remarks>
public sealed class AcpServer
{
    private const string ProtocolVersion = "0.1";

    private readonly IConversationStore _store;
    private readonly HeadlessRunner _runner;

    public AcpServer(IConversationStore store, HeadlessRunner runner)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <summary>Build the server with this protocol's methods registered.</summary>
    public JsonRpcServer Build()
        => new JsonRpcServer()
            .Handle("initialize", InitializeAsync)
            .Handle("session/new", NewSessionAsync)
            .Handle("session/prompt", PromptAsync)
            .Handle("session/history", HistoryAsync);

    private Task<JsonObject> InitializeAsync(JsonObject? parameters, CancellationToken cancellationToken)
        => Task.FromResult(new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["agentInfo"] = new JsonObject { ["name"] = "Concierge", ["version"] = "1.0" },
        });

    private async Task<JsonObject> NewSessionAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var owner = parameters?["ownerId"]?.GetValue<string>() ?? "local";
        var systemPrompt = parameters?["systemPrompt"]?.GetValue<string>();

        var conversation = await _store.StartAsync(owner, title: null, systemPrompt, cancellationToken)
            .ConfigureAwait(false);

        return new JsonObject { ["sessionId"] = conversation.Id.ToString() };
    }

    private async Task<JsonObject> PromptAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var conversationId = RequireSessionId(parameters);
        var prompt = parameters?["prompt"]?.GetValue<string>()
            ?? throw new InvalidOperationException("A prompt is required.");

        await _store.AppendEventAsync(conversationId, ConversationEventType.UserMessage, prompt, null, cancellationToken)
            .ConfigureAwait(false);

        var answered = await _runner.RunAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (!answered.Success)
        {
            throw new InvalidOperationException(answered.Error ?? "The run produced no answer.");
        }

        await _store.AppendEventAsync(
            conversationId,
            ConversationEventType.AssistantMessage,
            answered.Output,
            null,
            cancellationToken).ConfigureAwait(false);

        return new JsonObject { ["output"] = answered.Output };
    }

    private async Task<JsonObject> HistoryAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var conversationId = RequireSessionId(parameters);
        var turns = await _store.DeriveMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);

        var rendered = new JsonArray();
        foreach (var turn in turns)
        {
            rendered.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }

        return new JsonObject { ["turns"] = rendered };
    }

    /// <summary>
    /// Reads the session id, refusing anything that is not one. A caller that omits it is
    /// asking about no conversation in particular, which cannot be answered.
    /// </summary>
    private static Guid RequireSessionId(JsonObject? parameters)
    {
        var raw = parameters?["sessionId"]?.GetValue<string>();
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("A valid sessionId is required.");
    }
}
