using System.Text.Json.Nodes;

namespace Concierge.Shared.Rpc.Server;

/// <summary>Handles one method. Returns the result, or throws to produce an error reply.</summary>
public delegate Task<JsonObject> RpcMethod(JsonObject? parameters, CancellationToken cancellationToken);

/// <summary>
/// Serves JSON-RPC over a pair of streams, so something outside Concierge can drive it.
/// </summary>
/// <remarks>
/// <para>
/// One server, several vocabularies. The Agent Client Protocol and Concierge's own surface
/// are the same machinery with different method names registered on it, which is why neither
/// needed its own implementation.
/// </para>
/// <para>
/// A handler that throws becomes an error reply rather than ending the connection. The peer
/// is another program; a bad request from it must not take down the session serving it.
/// </para>
/// </remarks>
public sealed class JsonRpcServer
{
    /// <summary>The method does not exist. From the JSON-RPC error range.</summary>
    private const int MethodNotFound = -32601;

    /// <summary>The handler failed. Reserved for implementation-defined server errors.</summary>
    private const int InternalError = -32603;

    private readonly Dictionary<string, RpcMethod> _methods = new(StringComparer.Ordinal);
    private readonly IJsonRpcFraming _framing;

    /// <param name="framing">How messages are delimited. Line framing for ACP and stdio.</param>
    public JsonRpcServer(IJsonRpcFraming? framing = null) => _framing = framing ?? new LineFraming();

    /// <summary>The methods this server answers.</summary>
    public IReadOnlyCollection<string> Methods => _methods.Keys;

    /// <summary>Register a method. Re-registering a name replaces it.</summary>
    public JsonRpcServer Handle(string method, RpcMethod handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);

        _methods[method] = handler;
        return this;
    }

    /// <summary>Answer one message, or null when it needs no answer.</summary>
    public async Task<JsonRpcMessage?> HandleAsync(JsonRpcMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Method is not { } method)
        {
            return null;
        }

        // A notification is answered with silence, whatever happens to it. Replying would
        // leave the peer with an id it never issued.
        if (request.Id is not { } id)
        {
            if (_methods.TryGetValue(method, out var notified))
            {
                try
                {
                    await notified(request.Params, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Nowhere to report it, and nowhere it should reach.
                }
            }

            return null;
        }

        if (!_methods.TryGetValue(method, out var handler))
        {
            return JsonRpcMessage.Error(id, MethodNotFound, $"No such method '{method}'.");
        }

        try
        {
            var result = await handler(request.Params, cancellationToken).ConfigureAwait(false);
            return JsonRpcMessage.Reply(id, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The peer is another program. A bad request from it becomes an error reply, not
            // a dead connection.
            return JsonRpcMessage.Error(id, InternalError, exception.Message);
        }
    }

    /// <summary>Serve until the pipe closes or the caller stops it.</summary>
    public async Task ServeAsync(Stream inbound, Stream outbound, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(outbound);

        while (!cancellationToken.IsCancellationRequested)
        {
            var raw = await _framing.ReadAsync(inbound, cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                return;
            }

            var request = JsonRpcMessage.Parse(raw);
            if (request is null)
            {
                continue;
            }

            var reply = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                await _framing.WriteAsync(outbound, reply.ToJson(), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
