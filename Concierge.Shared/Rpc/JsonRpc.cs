using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Concierge.Shared.Rpc;

/// <summary>
/// One JSON-RPC 2.0 message: a request, a notification, or a reply.
/// </summary>
/// <remarks>
/// MCP, LSP, ACP and Concierge's own RPC surface are the same protocol with different method
/// names. This is written once so none of them needs a third-party library — and so none of
/// them can be taken away.
/// </remarks>
public sealed class JsonRpcMessage
{
    private const string Version = "2.0";

    private JsonRpcMessage(JsonObject payload) => Payload = payload;

    /// <summary>The raw message.</summary>
    public JsonObject Payload { get; }

    /// <summary>The correlation id, or null for a notification.</summary>
    public int? Id => Payload["id"]?.GetValue<int>();

    /// <summary>The method being called, or null for a reply.</summary>
    public string? Method => Payload["method"]?.GetValue<string>();

    /// <summary>The call's parameters, if any.</summary>
    public JsonObject? Params => Payload["params"] as JsonObject;

    /// <summary>The reply's result, if this is a successful reply.</summary>
    public JsonObject? Result => Payload["result"] as JsonObject;

    /// <summary>Whether this reply carries an error.</summary>
    public bool IsError => Payload["error"] is not null;

    /// <summary>The error text, when this is an error reply.</summary>
    public string? ErrorMessage => (Payload["error"] as JsonObject)?["message"]?.GetValue<string>();

    /// <summary>The error code, when this is an error reply.</summary>
    public int? ErrorCode => (Payload["error"] as JsonObject)?["code"]?.GetValue<int>();

    /// <summary>A call that expects a reply.</summary>
    public static JsonRpcMessage Request(int id, string method, JsonObject? parameters)
    {
        var payload = new JsonObject { ["jsonrpc"] = Version, ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }

        return new JsonRpcMessage(payload);
    }

    /// <summary>
    /// A call that expects no reply. It carries no id, because a peer given one would wait
    /// for an answer that is never sent.
    /// </summary>
    public static JsonRpcMessage Notification(string method, JsonObject? parameters)
    {
        var payload = new JsonObject { ["jsonrpc"] = Version, ["method"] = method };
        if (parameters is not null)
        {
            payload["params"] = parameters;
        }

        return new JsonRpcMessage(payload);
    }

    /// <summary>A successful reply.</summary>
    public static JsonRpcMessage Reply(int id, JsonObject result)
        => new(new JsonObject { ["jsonrpc"] = Version, ["id"] = id, ["result"] = result });

    /// <summary>A failed reply.</summary>
    public static JsonRpcMessage Error(int id, int code, string message)
        => new(new JsonObject
        {
            ["jsonrpc"] = Version,
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    /// <summary>Read a message, or null when the text is not one.</summary>
    /// <remarks>
    /// A peer can send anything. Returning null rather than throwing keeps one malformed
    /// frame from taking the connection down with it.
    /// </remarks>
    public static JsonRpcMessage? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) is JsonObject payload ? new JsonRpcMessage(payload) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Render the message for the wire.</summary>
    public string ToJson() => Payload.ToJsonString();
}

/// <summary>How messages are delimited on the wire.</summary>
/// <remarks>
/// Two framings exist because the protocols chose differently: MCP and ACP put one message
/// per line, LSP prefixes each with a byte count.
/// </remarks>
public interface IJsonRpcFraming
{
    /// <summary>Read the next message, or null when the pipe is finished.</summary>
    Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>Write one message.</summary>
    Task WriteAsync(Stream stream, string payload, CancellationToken cancellationToken = default);
}

/// <summary>One message per line. Used by MCP and ACP.</summary>
public sealed class LineFraming : IJsonRpcFraming
{
    /// <inheritdoc />
    public async Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var line = new List<byte>(256);
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            }

            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
            }

            line.Add(one[0]);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(Stream stream, string payload, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(payload + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A <c>Content-Length</c> header before each message. Used by LSP.</summary>
public sealed class HeaderFraming : IJsonRpcFraming
{
    /// <inheritdoc />
    public async Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var length = -1;

        while (true)
        {
            var header = await ReadHeaderLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (header is null)
            {
                return null;
            }

            if (header.Length == 0)
            {
                break;
            }

            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(header["Content-Length:".Length..].Trim(), out var parsed))
            {
                length = parsed;
            }
        }

        if (length < 0)
        {
            return null;
        }

        // The header counts bytes, not characters. Reading characters would truncate isiZulu,
        // Mandarin and emoji at the point where one character is several bytes.
        var body = new byte[length];
        var filled = 0;
        while (filled < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(filled, length - filled), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                // The peer promised more than it sent. A partial body is not a message.
                return null;
            }

            filled += read;
        }

        return Encoding.UTF8.GetString(body);
    }

    /// <inheritdoc />
    public async Task WriteAsync(Stream stream, string payload, CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new List<byte>(64);
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
            }

            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
            }

            line.Add(one[0]);
        }
    }
}

/// <summary>A peer answered with an error.</summary>
public sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message) : base($"{message} (code {code})") => Code = code;

    /// <summary>The protocol error code.</summary>
    public int Code { get; }
}

/// <summary>
/// Calls a JSON-RPC peer over a pair of streams.
/// </summary>
/// <remarks>
/// Replies are matched by id rather than by arrival order, because servers answer out of
/// order and two concurrent callers must not receive each other's answers. Every call is
/// bounded by a timeout: a peer that goes quiet must not hold a turn open indefinitely.
/// </remarks>
public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly Stream _outbound;
    private readonly Stream _inbound;
    private readonly IJsonRpcFraming _framing;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonRpcMessage>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _reader;
    private int _nextId;

    public JsonRpcClient(Stream outbound, Stream inbound, IJsonRpcFraming framing, TimeSpan? timeout = null)
    {
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        _framing = framing ?? throw new ArgumentNullException(nameof(framing));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Call a method and wait for its reply.</summary>
    /// <exception cref="JsonRpcException">The peer answered with an error.</exception>
    /// <exception cref="TimeoutException">The peer did not answer in time.</exception>
    public async Task<JsonObject?> InvokeAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;

        try
        {
            await SendAsync(JsonRpcMessage.Request(id, method, parameters), cancellationToken).ConfigureAwait(false);

            var reply = await pending.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            return reply.IsError
                ? throw new JsonRpcException(reply.ErrorCode ?? 0, reply.ErrorMessage ?? "The peer reported an error.")
                : reply.Result;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Send a message that expects no reply.</summary>
    public Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken = default)
        => SendAsync(JsonRpcMessage.Notification(method, parameters), cancellationToken);

    private async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _framing.WriteAsync(_outbound, message.ToJson(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var raw = await _framing.ReadAsync(_inbound, _shutdown.Token).ConfigureAwait(false);
                if (raw is null)
                {
                    break;
                }

                var message = JsonRpcMessage.Parse(raw);
                if (message?.Id is { } id && _pending.TryRemove(id, out var pending))
                {
                    pending.TrySetResult(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (IOException)
        {
            // The pipe closed under us; pending calls fall to their timeouts.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _shutdown.Dispose();
        _writeGate.Dispose();
    }
}
