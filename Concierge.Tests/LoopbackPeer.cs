using System.IO.Pipes;
using Concierge.Shared.Rpc;

namespace Concierge.Tests;

/// <summary>
/// A JSON-RPC peer in the same process, so protocol tests run with no server, no network and
/// no ports.
/// </summary>
/// <remarks>
/// Two anonymous pipes, one each way, and a loop that answers with whatever the test's
/// handler returns. A handler returning null answers nothing, which is how the timeout path
/// is exercised.
/// </remarks>
internal sealed class LoopbackPeer : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _clientToServerWrite;
    private readonly AnonymousPipeClientStream _clientToServerRead;
    private readonly AnonymousPipeServerStream _serverToClientWrite;
    private readonly AnonymousPipeClientStream _serverToClientRead;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    /// <param name="handler">Answers a request, or returns null to stay silent.</param>
    /// <param name="answerInReverse">
    /// Hold replies until several have queued and answer them backwards, reproducing a server
    /// that responds out of order.
    /// </param>
    public LoopbackPeer(Func<JsonRpcMessage, JsonRpcMessage?> handler, bool answerInReverse = false)
    {
        _clientToServerWrite = new AnonymousPipeServerStream(PipeDirection.Out);
        _clientToServerRead = new AnonymousPipeClientStream(
            PipeDirection.In,
            _clientToServerWrite.GetClientHandleAsString());

        _serverToClientWrite = new AnonymousPipeServerStream(PipeDirection.Out);
        _serverToClientRead = new AnonymousPipeClientStream(
            PipeDirection.In,
            _serverToClientWrite.GetClientHandleAsString());

        _loop = Task.Run(() => ServeAsync(handler, answerInReverse));
    }

    /// <summary>The stream a client writes its requests to.</summary>
    public Stream ClientToServer => _clientToServerWrite;

    /// <summary>The stream a client reads its replies from.</summary>
    public Stream ServerToClient => _serverToClientRead;

    private async Task ServeAsync(Func<JsonRpcMessage, JsonRpcMessage?> handler, bool answerInReverse)
    {
        var framing = new LineFraming();
        var held = new List<JsonRpcMessage>();

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
                if (request?.Id is null)
                {
                    continue;
                }

                var reply = handler(request);
                if (reply is null)
                {
                    continue;
                }

                if (!answerInReverse)
                {
                    await framing.WriteAsync(_serverToClientWrite, reply.ToJson(), _shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                held.Add(reply);
                if (held.Count < 3)
                {
                    continue;
                }

                held.Reverse();
                foreach (var queued in held)
                {
                    await framing.WriteAsync(_serverToClientWrite, queued.ToJson(), _shutdown.Token).ConfigureAwait(false);
                }

                held.Clear();
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
