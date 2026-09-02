using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Concierge.Shared.Terminals;

/// <summary>
/// Keeps shell sessions open across calls, so what one command establishes is still there
/// for the next.
/// </summary>
/// <remarks>
/// The harness's <c>RunCommandAsync</c> spawns a process per command, so nothing survives —
/// not the working directory, not an environment variable, not a running interpreter. Some
/// work needs continuity, and for that the session has to outlive the call.
/// </remarks>
public interface ITerminalRuntime
{
    /// <summary>The sessions currently open.</summary>
    IReadOnlyCollection<string> Sessions { get; }

    /// <summary>Open a session and return its handle.</summary>
    Task<string> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Run one command in an open session and return what it printed.</summary>
    Task<string> SendAsync(string sessionId, string command, CancellationToken cancellationToken = default);

    /// <summary>Close a session. Unknown handles are ignored.</summary>
    Task CloseAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Terminal sessions backed by a long-lived shell process.
/// </summary>
/// <remarks>
/// <para>
/// Output is delimited by a sentinel echoed after each command, because a shell's output
/// stream has no other end-of-command marker: without one, a read either stops early and
/// truncates, or blocks forever waiting for a prompt that is not coming.
/// </para>
/// <para>
/// Each session's own read timeout bounds a command that never returns. A phone must not be
/// left with a shell holding its stdout open indefinitely.
/// </para>
/// </remarks>
public sealed class TerminalRuntime : ITerminalRuntime, IDisposable
{
    private sealed class Session(Process process, StreamWriter input) : IDisposable
    {
        public Process Process { get; } = process;
        public StreamWriter Input { get; } = input;
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // Already gone between the check and the kill.
            }

            Input.Dispose();
            Process.Dispose();
            Gate.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly string _workingDirectory;
    private readonly TimeSpan _readTimeout;

    /// <param name="workingDirectory">Where sessions start.</param>
    /// <param name="readTimeout">How long to wait for a command's output before giving up.</param>
    public TerminalRuntime(string workingDirectory, TimeSpan? readTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _workingDirectory = workingDirectory;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Sessions => _sessions.Keys.ToList();

    /// <inheritdoc />
    public Task<string> OpenAsync(CancellationToken cancellationToken = default)
    {
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = _workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        var id = $"term-{Guid.NewGuid():N}";
        _sessions[id] = new Session(process, process.StandardInput);
        return Task.FromResult(id);
    }

    /// <inheritdoc />
    public async Task<string> SendAsync(string sessionId, string command, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId ?? string.Empty, out var session))
        {
            throw new InvalidOperationException($"No terminal session '{sessionId}' is open.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A sentinel echoed after the command is the only reliable end marker: the shell
            // gives no other signal that one command's output has finished.
            var sentinel = $"__concierge_done_{Guid.NewGuid():N}__";

            await session.Input.WriteLineAsync(command).ConfigureAwait(false);
            await session.Input.WriteLineAsync($"echo {sentinel}").ConfigureAwait(false);
            await session.Input.FlushAsync(cancellationToken).ConfigureAwait(false);

            return await ReadUntilAsync(session, sentinel, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <inheritdoc />
    public Task CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(sessionId ?? string.Empty, out var session))
        {
            session.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(id, out var session))
            {
                session.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads output lines until the sentinel appears, or the read timeout elapses.
    /// </summary>
    private async Task<string> ReadUntilAsync(Session session, string sentinel, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);

        var output = new StringBuilder();
        try
        {
            while (true)
            {
                var line = await session.Process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    // The shell exited. Whatever was printed before that is still the answer.
                    break;
                }

                // The echoed sentinel command appears in the stream before its output when
                // the shell echoes input, so only a bare match ends the read.
                if (line.Trim() == sentinel)
                {
                    break;
                }

                if (line.Contains(sentinel, StringComparison.Ordinal))
                {
                    continue;
                }

                output.AppendLine(line);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The command never finished. Returning what it printed beats leaving the
            // caller blocked on a shell that is not coming back.
            output.AppendLine($"[no output after {_readTimeout.TotalSeconds:0} seconds]");
        }

        return output.ToString();
    }
}
