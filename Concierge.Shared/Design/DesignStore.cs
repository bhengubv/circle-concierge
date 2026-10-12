using System.Text.Json;

namespace Concierge.Shared.Design;

/// <summary>What came back off disk, and what went wrong if nothing did.</summary>
/// <param name="Document">The design, or null when there was none to open.</param>
/// <param name="Problem">
/// Why there is no document, when the reason is worth telling somebody. Null when
/// there simply was not one saved yet, which is not a problem.
/// </param>
public sealed record DesignRestore(DesignDocument? Document, string? Problem);

/// <summary>A saved design session, or the reason it could not be restored.</summary>
public sealed record DesignSessionRestore(DesignSessionSnapshot? Session, string? Problem);

/// <summary>Somewhere a design survives being closed.</summary>
public interface IDesignStore
{
    /// <summary>The design as it was left, if there is one.</summary>
    Task<DesignRestore> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores canvas history where supported. Older/custom stores fall back to
    /// the current document as a one-moment session.
    /// </summary>
    async Task<DesignSessionRestore> LoadSessionAsync(CancellationToken cancellationToken = default)
    {
        var restore = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = restore.Document is { } document
            ? new DesignSessionSnapshot(
                [new DesignMoment(document, "Back as you left it", DateTimeOffset.UtcNow)], 0, null)
            : null;
        return new DesignSessionRestore(snapshot, restore.Problem);
    }

    /// <summary>Keeps the design as it stands.</summary>
    Task SaveAsync(DesignDocument document, CancellationToken cancellationToken = default);

    /// <summary>Saves the bounded history and current position where supported.</summary>
    Task SaveSessionAsync(DesignSessionSnapshot session, CancellationToken cancellationToken = default)
        => session.Moments.Count == 0
            ? Task.CompletedTask
            : SaveAsync(session.Moments[Math.Clamp(session.Position, 0, session.Moments.Count - 1)].Document,
                cancellationToken);

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
/// **The bounded session is saved.** A session keeps at most thirty moments,
/// including the first state, and stores the active position and selection so
/// undo, redo, and the visual history return after a restart.
///
/// **A design that cannot be read never blocks the canvas.** It opens blank and
/// says why. The alternative — refusing to open — leaves somebody with no way back
/// to a working surface, and the file they cannot open is still on disk either way.
/// </summary>
public sealed class FileDesignStore : IDesignStore
{
    private const int CurrentSessionVersion = 1;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions SessionJson = new() { WriteIndented = true };

    private sealed record SessionWire(int SessionSchemaVersion, int Position, string? Selected,
        IReadOnlyList<MomentWire> Moments);
    private sealed record MomentWire(string Document, string What, DateTimeOffset At);

    public FileDesignStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Where it is written, so a person can find or delete it.</summary>
    public string Path => _path;

    public async Task<DesignRestore> LoadAsync(CancellationToken cancellationToken = default)
    {
        var session = await LoadSessionAsync(cancellationToken).ConfigureAwait(false);
        var selected = session.Session is { } snapshot
            ? snapshot.Moments[Math.Clamp(snapshot.Position, 0, snapshot.Moments.Count - 1)].Document
            : null;
        return new DesignRestore(selected, session.Problem);
    }

    public async Task<DesignSessionRestore> LoadSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(_path))
            {
                // Nothing saved yet is the ordinary case on a first run, not a
                // fault, so it carries no message to show anybody.
                return new DesignSessionRestore(null, null);
            }

            var written = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(written);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty(nameof(SessionWire.SessionSchemaVersion), out _))
            {
                SessionWire? wire;
                try
                {
                    wire = JsonSerializer.Deserialize<SessionWire>(written, SessionJson);
                }
                catch (JsonException error)
                {
                    return new DesignSessionRestore(null, $"The saved design history could not be read: {error.Message}");
                }

                if (wire is null || wire.SessionSchemaVersion <= 0)
                    return new DesignSessionRestore(null, "The saved design history has no valid version.");
                if (wire.SessionSchemaVersion > CurrentSessionVersion)
                    return new DesignSessionRestore(null,
                        $"This design history was saved by a newer Concierge (version {wire.SessionSchemaVersion}; this one reads up to {CurrentSessionVersion}). It has been left alone rather than opened incompletely.");
                if (wire.Moments is null || wire.Moments.Count == 0 || wire.Moments.Count > DesignSession.MaxMoments
                    || wire.Position < 0 || wire.Position >= wire.Moments.Count)
                    return new DesignSessionRestore(null, "The saved design history has an invalid length or position.");

                var moments = new List<DesignMoment>(wire.Moments.Count);
                foreach (var moment in wire.Moments)
                {
                    if (moment is null)
                        return new DesignSessionRestore(null, "One moment in the saved design history is empty.");
                    if (!DesignDocumentFormat.TryDeserialize(moment.Document, out var document, out var problem))
                        return new DesignSessionRestore(null,
                            $"One moment in the saved design history could not be opened: {problem}");
                    moments.Add(new DesignMoment(document!, moment.What ?? "Edited", moment.At));
                }

                var current = moments[wire.Position].Document;
                var selected = wire.Selected is not null && current.Find(wire.Selected) is not null ? wire.Selected : null;
                return new DesignSessionRestore(
                    new DesignSessionSnapshot(moments, wire.Position, selected), null);
            }

            // Version 1 of the original store was a single DesignDocumentFormat file.
            // Continue opening it and wrap it as the first retained moment.
            return DesignDocumentFormat.TryDeserialize(written, out var legacy, out var legacyProblem)
                ? new DesignSessionRestore(
                    new DesignSessionSnapshot([new DesignMoment(legacy!, "Back as you left it", DateTimeOffset.UtcNow)], 0, null), null)
                : new DesignSessionRestore(null, legacyProblem);
        }
        catch (JsonException error)
        {
            return new DesignSessionRestore(null, $"The saved design could not be read: {error.Message}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new DesignSessionRestore(null, $"The saved design could not be read: {error.Message}");
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
            var existing = await ReadSessionCoreAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Problem is not null)
                throw new IOException(existing.Problem);
            var session = existing.Session is { } snapshot
                ? new DesignSession(snapshot)
                : new DesignSession(document);
            if (existing.Session is not null
                && !string.Equals(DesignDocumentFormat.Serialize(session.Current), DesignDocumentFormat.Serialize(document), StringComparison.Ordinal))
                session.Record(document, "Updated from a connected device");

            await WriteSessionCoreAsync(session.Capture(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSessionAsync(DesignSessionSnapshot session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Moments is null || session.Moments.Count == 0)
            throw new ArgumentException("A design session must contain at least one moment.", nameof(session));
        if (session.Moments.Count > DesignSession.MaxMoments)
            throw new ArgumentException($"A design session can retain at most {DesignSession.MaxMoments} moments.", nameof(session));
        if (session.Position < 0 || session.Position >= session.Moments.Count)
            throw new ArgumentOutOfRangeException(nameof(session), "The design session position is outside its history.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = await ReadSessionCoreAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Problem is not null)
                throw new IOException(existing.Problem);
            await WriteSessionCoreAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DesignSessionRestore> ReadSessionCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new DesignSessionRestore(null, null);

        var written = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(written);
        }
        catch (JsonException error)
        {
            return new DesignSessionRestore(null, $"The saved design could not be read: {error.Message}");
        }
        using (json)
        {
        if (json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty(nameof(SessionWire.SessionSchemaVersion), out _))
        {
            var wire = JsonSerializer.Deserialize<SessionWire>(written, SessionJson);
            if (wire is null || wire.SessionSchemaVersion != CurrentSessionVersion
                || wire.Moments is null || wire.Moments.Count == 0 || wire.Moments.Count > DesignSession.MaxMoments
                || wire.Position < 0 || wire.Position >= wire.Moments.Count)
                return new DesignSessionRestore(null, "The saved design history has an unsupported version or invalid shape.");

            var moments = new List<DesignMoment>(wire.Moments.Count);
            foreach (var moment in wire.Moments)
            {
                if (moment is null)
                    return new DesignSessionRestore(null, "One moment in the saved design history is empty.");
                if (!DesignDocumentFormat.TryDeserialize(moment.Document, out var document, out var problem))
                    return new DesignSessionRestore(null, problem ?? "One moment in the saved design history is invalid.");
                moments.Add(new DesignMoment(document!, moment.What ?? "Edited", moment.At));
            }
            return new DesignSessionRestore(
                new DesignSessionSnapshot(moments, wire.Position, wire.Selected), null);
        }

        return DesignDocumentFormat.TryDeserialize(written, out var legacy, out var legacyProblem)
            ? new DesignSessionRestore(
                new DesignSessionSnapshot([new DesignMoment(legacy!, "Back as you left it", DateTimeOffset.UtcNow)], 0, null), null)
            : new DesignSessionRestore(null, legacyProblem);
        }
    }

    private async Task WriteSessionCoreAsync(DesignSessionSnapshot session, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var wire = new SessionWire(
            CurrentSessionVersion,
            session.Position,
            session.Selected,
            session.Moments.Select(moment => new MomentWire(
                DesignDocumentFormat.Serialize(moment.Document), moment.What, moment.At)).ToArray());
        var staging = _path + ".writing";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(wire, SessionJson), cancellationToken)
            .ConfigureAwait(false);
        File.Move(staging, _path, overwrite: true);
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
