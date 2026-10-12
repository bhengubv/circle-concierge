using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Concierge.Shared.Away;

/// <summary>
/// Durable receipts for requests delivered over Away. The client keeps the request ID in its
/// outbox; the host keeps the final answer so a lost HTTP response cannot run the same action
/// twice. An interrupted receipt is deliberately not replayed: the conversation must be
/// checked before a person submits a fresh request.
/// </summary>
public sealed class AwayRequestLedger
{
    private sealed class GateEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public readonly object Sync = new();
        public int Users;
        public bool Retired;
    }

    private sealed record Receipt(string Fingerprint, string State, AwayAnswer? Answer);

    private readonly ConcurrentDictionary<string, GateEntry> _gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _folder;

    public AwayRequestLedger(string? folder = null)
        => _folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Concierge", "away-requests");

    public static string Fingerprint(AwaySaid said)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(said, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(serialized));
    }

    public async Task<AwayAnswer> ExecuteOnceAsync(Guid requestId, string fingerprint,
        Func<Task<AwayAnswer>> execute, CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request ID is required.", nameof(requestId));
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(execute);

        var path = Path.Combine(_folder, $"{requestId:N}.json");
        GateEntry gate;
        while (true)
        {
            gate = _gates.GetOrAdd(path, static _ => new GateEntry());
            lock (gate.Sync)
            {
                if (gate.Retired) continue;
                gate.Users++;
                break;
            }
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseGate(path, gate, entered: false);
            throw;
        }
        try
        {
            Directory.CreateDirectory(_folder);

            // The in-process gate above does not coordinate a second Desktop host.
            // Lock a stable per-request file range as well, so two hosts receiving the
            // same outbox retry cannot both observe a missing receipt and execute it.
            await using var processLock = await AcquireProcessLockAsync(path + ".lock", cancellationToken)
                .ConfigureAwait(false);

            if (File.Exists(path))
            {
                var receipt = JsonSerializer.Deserialize<Receipt>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
                if (receipt is null || !string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return new AwayAnswer(false, string.Empty, "This saved request ID belongs to different work.");
                if (receipt.State == "completed" && receipt.Answer is not null)
                    return receipt.Answer;

                return new AwayAnswer(false, string.Empty,
                    "This request was interrupted after it reached Concierge. Check the conversation before trying it again.");
            }

            await WriteAsync(path, new Receipt(fingerprint, "started", null), cancellationToken).ConfigureAwait(false);
            var answer = await execute().ConfigureAwait(false);
            // Accepted work outlives the caller's HTTP connection. Persist its
            // final receipt even if the watch slept or disconnected while it ran.
            // A lost response must replay this answer, not become an interrupted run.
            await WriteAsync(path, new Receipt(fingerprint, "completed", answer), CancellationToken.None).ConfigureAwait(false);
            return answer;
        }
        finally
        {
            ReleaseGate(path, gate, entered: true);
        }
    }

    private void ReleaseGate(string path, GateEntry gate, bool entered)
    {
        if (entered) gate.Semaphore.Release();
        lock (gate.Sync)
        {
            gate.Users--;
            if (gate.Users == 0)
            {
                gate.Retired = true;
                _gates.TryRemove(path, out _);
                gate.Semaphore.Dispose();
            }
        }
    }

    private static async Task WriteAsync(string path, Receipt receipt, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".writing";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt, JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<FileStream> AcquireProcessLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        stream.Lock(0, 1);
                        return stream;
                    }
                    catch (IOException failure) when (IsLockContention(failure))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
                    if (LockUnixFileDescriptor(descriptor, LockExclusive | LockNonBlocking) == 0)
                        return stream;

                    var nativeCode = Marshal.GetLastPInvokeError();
                    if (nativeCode is not (11 or 13 or 35))
                        throw new IOException($"Could not lock the request receipt file (native error {nativeCode}).");

                    await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsLockContention(IOException failure)
    {
        // EAGAIN/EACCES on Unix (macOS may report 35), and ERROR_LOCK_VIOLATION /
        // ERROR_SHARING_VIOLATION on Windows. Other I/O failures must be surfaced.
        var nativeCode = failure.HResult & 0xFFFF;
        return nativeCode is 11 or 13 or 32 or 33 or 35;
    }

    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int LockUnixFileDescriptor(int fileDescriptor, int operation);
}
