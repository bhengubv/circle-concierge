using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Concierge.Shared.Chat;
using Concierge.Shared.Chat.Isolation;

namespace Concierge.Ai.Isolated;

/// <summary>
/// The on-device model, run in a child process so a fault in it cannot take
/// Concierge with it.
///
/// The problem this solves is specific. The generator is native code reached
/// through P/Invoke, and it faults with an access violation inside
/// mnn_llm_generate_stream_text. An access violation is not a managed
/// exception: by the time anything could catch it the process is already
/// being torn down. No amount of try/catch around the call helps, and the
/// codebase has a comment claiming otherwise that was written before anyone
/// tried it.
///
/// So the model runs somewhere else. When it faults, that process dies, this
/// one notices a closed pipe, and the conversation gets a sentence explaining
/// what happened instead of the application disappearing mid-sentence.
///
/// What this deliberately does not do is restart the child mid-reply and try
/// again. The reply that faulted is gone, the state that produced it is gone,
/// and quietly re-running a generation that just crashed the model is how you
/// get a loop. The next message starts a fresh child.
/// </summary>
public sealed class IsolatedChatRuntime : IChatRuntime, IDisposable, IAsyncDisposable
{
    private readonly IsolatedRuntimeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private StreamWriter? _toChild;
    private int _nextRequestId;

    private string _engineLabel = "On-device model";
    private bool _isReady;
    private string _status = "Not started yet.";

    /// <summary>Set when the child has faulted, so the status says something
    /// truthful rather than "ready" about a process that is gone.</summary>
    private bool _childFaulted;

    public IsolatedChatRuntime(IsolatedRuntimeOptions? options = null)
        => _options = options ?? new IsolatedRuntimeOptions();

    // Same identity as the in-process runtime it replaces: everything that
    // routes by id — the provider picker, the settings panel — keeps working.
    public string Id => "circleai";

    /// <summary>
    /// Still on the device. The model moved to a child process so a native fault
    /// costs the sentence rather than the application — it did not move machines.
    /// </summary>
    public bool LeavesDevice => false;

    public string EngineLabel => _engineLabel;

    public bool IsReady => _isReady && !_childFaulted;

    public string StatusMessage => _status;

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Setting up is done outside the iterator: C# will not let a yield sit
        // in a catch, and every failure here needs to become a sentence in the
        // conversation rather than an exception into the composer.
        var (started, requestId, chunks, failure) = await BeginAsync(messages, cancellationToken)
            .ConfigureAwait(false);

        if (!started)
        {
            yield return $"[{failure}]";
            yield break;
        }

        // Cancelling asks the child to stop rather than killing it: a model
        // that stopped when asked is still a good model to keep loaded.
        using var registration = cancellationToken.Register(() => _ = CancelAsync(requestId));

        // Read with None, not the caller's token: when the caller cancels, the
        // child still owes a Done, and the pump still owes the closing note if
        // it faulted. Tearing the channel down here would lose both.
        await foreach (var chunk in chunks!.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Starts the child if needed and posts the request. Returns why it could
    /// not rather than throwing, because every caller of this turns a failure
    /// into text for the person to read.
    /// </summary>
    private async Task<(bool Started, int RequestId, Channel<string>? Chunks, string Failure)> BeginAsync(
        IReadOnlyList<ChatTurn> messages, CancellationToken cancellationToken)
    {
        var chunks = Channel.CreateUnbounded<string>();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureChildAsync().ConfigureAwait(false))
            {
                return (false, 0, null, _status);
            }

            var requestId = ++_nextRequestId;
            _pending = chunks;

            var request = new ModelHostRequest(
                ModelHostProtocol.Stream,
                requestId,
                messages.Select(ToWire).ToArray());

            try
            {
                await _toChild!.WriteLineAsync(JsonSerializer.Serialize(request, ModelHostProtocol.Json))
                               .ConfigureAwait(false);
                await _toChild.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The child went between the check and the write.
                MarkFaulted();
                _pending = null;
                return (false, 0, null, _status);
            }

            return (true, requestId, chunks, string.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Channel<string>? _pending;

    private static ModelHostTurn ToWire(ChatTurn turn)
        => new(turn.Role, turn.Content,
            turn.Images?.Select(i => new ModelHostImage(i.FileName, i.MediaType, Convert.ToBase64String(i.Bytes))).ToArray());

    // ── The child ─────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the child and waits for it to say hello.
    ///
    /// Needed because the composer stays disabled until IsReady, and IsReady
    /// only becomes true once the child has loaded — so starting lazily on the
    /// first message means the first message can never be sent. The in-process
    /// runtime this replaces had a hosted service for the same reason.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureChildAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts the child if it is not running. Returns false with a readable
    /// status when it cannot be started at all — a missing executable is a
    /// deployment problem, and saying so beats a silent dead composer.
    /// </summary>
    private async Task<bool> EnsureChildAsync()
    {
        if (_process is { HasExited: false })
        {
            return true;
        }

        var executable = _options.ResolveHostPath();
        if (executable is null)
        {
            _status = "The model host is missing from this install, so nothing can answer on this device.";
            _isReady = false;
            return false;
        }

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };

        try
        {
            _process = Process.Start(start);
        }
        catch (Exception ex)
        {
            _status = $"The model host would not start: {ex.Message}";
            _isReady = false;
            return false;
        }

        if (_process is null)
        {
            _status = "The model host would not start.";
            _isReady = false;
            return false;
        }

        _childFaulted = false;
        _toChild = _process.StandardInput;

        // Read the child's stdout on its own loop for the life of the process.
        _ = Task.Run(() => PumpAsync(_process));

        // And drain stderr, which is redirected and would otherwise fill.
        // A redirected pipe nobody reads blocks the writer once the buffer is
        // full — the child would wedge partway through loading, which looks
        // exactly like a model that is slow rather than one that is stuck.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                    // Diagnostics from the child. Read and dropped: the parent
                    // has no log to put them in, and the point is to keep the
                    // pipe empty.
                }
            }
            catch (Exception)
            {
                // The process went. The stdout pump handles that.
            }
        });

        // The hello carries the label and whether the model loaded. Bounded:
        // a child that never says hello must not hang the composer forever.
        var deadline = DateTimeOffset.UtcNow + _options.StartTimeout;
        while (!_isReady && !_childFaulted && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        if (!_isReady && !_childFaulted)
        {
            _status = "The model did not finish loading in time.";
        }

        if (_isReady)
        {
            // A child that came up healthy earns the budget back, so a fault an
            // hour ago does not count against one tonight.
            _restarts = 0;
        }

        return _isReady;
    }

    /// <summary>
    /// Reads the child until it stops. Ending is not an error path — it is the
    /// case this class exists for, and it is handled here rather than thrown.
    /// </summary>
    private async Task PumpAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                ModelHostResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<ModelHostResponse>(line, ModelHostProtocol.Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (response is null)
                {
                    continue;
                }

                if (response.EngineLabel is not null)
                {
                    _engineLabel = response.EngineLabel;
                    _status = response.Status ?? _status;
                    _isReady = response.Ready;
                    continue;
                }

                if (response.Chunk is { Length: > 0 })
                {
                    _pending?.Writer.TryWrite(response.Chunk);
                }

                if (response.Error is { Length: > 0 })
                {
                    _pending?.Writer.TryWrite($"[{response.Error}]");
                }

                if (response.Done)
                {
                    _pending?.Writer.TryComplete();
                    _pending = null;
                }
            }
        }
        catch (Exception)
        {
            // Reading failed because the process went. Same handling as a
            // clean end of stream.
        }

        // Out of the loop means the child is gone. If a reply was in flight,
        // it faulted — say so in the conversation rather than leaving the
        // composer spinning on a stream that will never produce another token.
        if (_pending is { } waiting)
        {
            MarkFaulted();
            waiting.Writer.TryWrite(
                "\n\n[The model stopped unexpectedly. Concierge is still running — send the message again to try once more.]");
            waiting.Writer.TryComplete();
            _pending = null;
        }
        else if (process.HasExited && process.ExitCode != 0)
        {
            MarkFaulted();
        }
    }

    /// <summary>How many times the child has been restarted after a fault.
    /// Capped, because a model that faults while loading would otherwise be
    /// restarted forever.</summary>
    private int _restarts;

    private const int MaxRestarts = 3;

    private void MarkFaulted()
    {
        _childFaulted = true;
        _isReady = false;
        _status = "The model stopped unexpectedly.";

        // Restart it, rather than waiting to be asked.
        //
        // The composer is disabled while IsReady is false, so "send another
        // message and it will start again" is advice nobody can follow — the
        // control that would send it is greyed out. Without this the app
        // survives the fault and is then useless until it is restarted, which
        // is only half the point.
        //
        // Capped and not looped: a model that faults during loading would
        // otherwise be restarted until the machine gave up.
        if (_restarts >= MaxRestarts)
        {
            _status = "The model stopped repeatedly, so it has not been started again. Restart Concierge to try once more.";
            return;
        }

        _restarts++;

        _ = Task.Run(async () =>
        {
            // A moment first: restarting instantly into whatever just faulted
            // tends to fault again.
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            try
            {
                await WarmUpAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // EnsureChildAsync records why in _status.
            }
        });
    }

    private async Task CancelAsync(int requestId)
    {
        try
        {
            if (_toChild is null || _process is null || _process.HasExited)
            {
                return;
            }

            await _toChild.WriteLineAsync(JsonSerializer.Serialize(
                new ModelHostRequest(ModelHostProtocol.Cancel, requestId), ModelHostProtocol.Json)).ConfigureAwait(false);
            await _toChild.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelling a child that has already gone is not worth reporting.
        }
    }

    /// <summary>
    /// Both, deliberately. A service registered only as IAsyncDisposable makes
    /// the DI container refuse to be disposed synchronously — which is how a
    /// host may well shut down, and the failure arrives at process exit where
    /// it is least welcome. Stopping a child process needs no await anyway.
    /// </summary>
    public void Dispose() => Stop();

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                // Closing stdin asks it to stop; killing is the fallback for
                // one that is wedged inside native code and will not read.
                _toChild?.Close();

                if (!_process.WaitForExit(2000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception)
        {
            // Shutting down. Nothing here is worth failing over.
        }

        _process?.Dispose();
        _gate.Dispose();
    }
}
