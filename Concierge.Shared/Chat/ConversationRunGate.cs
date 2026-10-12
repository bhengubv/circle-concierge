namespace Concierge.Shared.Chat;

/// <summary>Serializes distinct turns for one conversation within a Concierge process.</summary>
/// <remarks>
/// A turn can continue after its workspace is no longer visible. A second request
/// must not enter the conversation history before the first answer is recorded.
/// This gate protects in-process ordering only; separate hosts need database-backed
/// coordination before they can safely write to one conversation concurrently.
/// </remarks>
public sealed class ConversationRunGate
{
    private readonly string _lockDirectory;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    public ConversationRunGate(string? lockDirectory = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.GetTempPath();
        _lockDirectory = lockDirectory ?? Path.Combine(local, "Concierge", "conversation-run-locks");
    }

    /// <summary>Waits until no other turn is executing for this conversation.</summary>
    public async ValueTask<IDisposable> EnterAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(conversationId, out entry!))
            {
                entry = new Entry();
                _entries.Add(conversationId, entry);
            }

            entry.References++;
        }

        var entered = false;
        FileStream? processLock = null;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            processLock = await AcquireProcessLockAsync(conversationId, cancellationToken).ConfigureAwait(false);
            return new Lease(this, conversationId, entry, processLock);
        }
        catch
        {
            processLock?.Dispose();
            if (entered)
                entry.Semaphore.Release();
            ReleaseReference(conversationId, entry);
            throw;
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_lockDirectory);
        var path = Path.Combine(_lockDirectory, $"{conversationId:N}.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // FileShare.None is released by the operating system when the owning
                // process exits, including a crash. Keep the empty lock file: deleting
                // it while another process has opened it can split waiters across two
                // file identities and admit concurrent turns.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException exception) when (IsSharingConflict(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingConflict(IOException exception)
    {
        var nativeCode = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows()
            ? nativeCode is 32 or 33 // sharing / lock violation
            : nativeCode is 11 or 35; // EAGAIN / EWOULDBLOCK
    }

    private void Release(Guid conversationId, Entry entry, FileStream processLock)
    {
        processLock.Dispose();
        entry.Semaphore.Release();
        ReleaseReference(conversationId, entry);
    }

    private void ReleaseReference(Guid conversationId, Entry entry)
    {
        lock (_sync)
        {
            entry.References--;
            if (entry.References == 0
                && _entries.TryGetValue(conversationId, out var current)
                && ReferenceEquals(current, entry))
            {
                _entries.Remove(conversationId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Lease(ConversationRunGate owner, Guid conversationId, Entry entry, FileStream processLock) : IDisposable
    {
        private ConversationRunGate? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(conversationId, entry, processLock);
        }
    }
}
