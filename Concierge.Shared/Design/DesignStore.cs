namespace Concierge.Shared.Design;

/// <summary>What came back off disk, and what went wrong if nothing did.</summary>
/// <param name="Document">The design, or null when there was none to open.</param>
/// <param name="Problem">
/// Why there is no document, when the reason is worth telling somebody. Null when
/// there simply was not one saved yet, which is not a problem.
/// </param>
public sealed record DesignRestore(DesignDocument? Document, string? Problem);

/// <summary>Somewhere a design survives being closed.</summary>
public interface IDesignStore
{
    /// <summary>The design as it was left, if there is one.</summary>
    Task<DesignRestore> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Keeps the design as it stands.</summary>
    Task SaveAsync(DesignDocument document, CancellationToken cancellationToken = default);

    /// <summary>Forgets it.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A design, on disk.
///
/// The canvas kept everything in memory, so closing the app threw the work away —
/// and `DesignDocumentFormat`, which exists precisely to write one down, was
/// referenced by nothing. That is the shape this repository keeps finding:
/// `McpClient`, `FileTodoStore`, `ProcessHookBridge`, the sandbox,
/// `EstimatedTokens`. This one was mine, added the same day.
///
/// Two decisions worth stating rather than discovering later.
///
/// **The current document is saved, not the history.** A session keeps thirty
/// moments so going back is free, and writing all thirty on every keystroke would
/// turn a canvas into a disk benchmark. What survives a restart is the design;
/// what does not is the ability to undo past the moment you closed it. That is a
/// real loss and the honest trade — say so rather than implying the history came
/// back.
///
/// **A design that cannot be read never blocks the canvas.** It opens blank and
/// says why. The alternative — refusing to open — leaves somebody with no way back
/// to a working surface, and the file they cannot open is still on disk either way.
/// </summary>
public sealed class FileDesignStore : IDesignStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileDesignStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Where it is written, so a person can find or delete it.</summary>
    public string Path => _path;

    public async Task<DesignRestore> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(_path))
            {
                // Nothing saved yet is the ordinary case on a first run, not a
                // fault, so it carries no message to show anybody.
                return new DesignRestore(null, null);
            }

            var written = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);

            return DesignDocumentFormat.TryDeserialize(written, out var document, out var problem)
                ? new DesignRestore(document, null)
                : new DesignRestore(null, problem);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new DesignRestore(null, $"The saved design could not be read: {error.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DesignDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside and moved into place. A save interrupted halfway —
            // the app closing, the machine sleeping — would otherwise leave a
            // half-written file where the design used to be, and the next open
            // would report it unreadable and start blank. Losing the last change
            // is recoverable; losing the design is not.
            var staging = _path + ".writing";

            await File.WriteAllTextAsync(
                staging, DesignDocumentFormat.Serialize(document), cancellationToken).ConfigureAwait(false);

            File.Move(staging, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Nothing to tell anybody. Clearing is housekeeping, and a design that
            // refuses to be deleted is not a thing somebody can act on.
        }
        finally
        {
            _gate.Release();
        }
    }
}
