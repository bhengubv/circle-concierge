using System.Runtime.InteropServices;
using System.Text.Json;

namespace Concierge.Shared;

public sealed record PersistedConciergeState(
    int SchemaVersion,
    DateTimeOffset SavedAt,
    ConciergeSnapshot Snapshot,
    BeyondClaudeSnapshot BeyondClaude);

public interface IAppStatePersistenceService
{
    string StatePath { get; }

    Task SaveAsync(PersistedConciergeState state, CancellationToken cancellationToken = default);

    Task<PersistedConciergeState?> LoadAsync(CancellationToken cancellationToken = default);
}

public sealed class AppStatePersistenceService : IAppStatePersistenceService
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _stateDirectory;

    public AppStatePersistenceService()
        : this(Path.Combine(Directory.GetCurrentDirectory(), ".concierge-artifacts", "state"))
    {
    }

    public AppStatePersistenceService(string stateDirectory)
    {
        _stateDirectory = stateDirectory;
        StatePath = Path.Combine(_stateDirectory, "concierge-state.v1.json");
    }

    public string StatePath { get; }

    public async Task SaveAsync(PersistedConciergeState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException(
                $"Schema version {state.SchemaVersion} does not match current {CurrentSchemaVersion}.",
                nameof(state));
        }

        Directory.CreateDirectory(_stateDirectory);
        var tempPath = StatePath + ".tmp";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            TryRestrictPermissions(tempPath);

            // File.Move with overwrite uses ReplaceFile/MoveFileEx on Windows and rename() on POSIX —
            // both atomic on a single volume. Readers either see the prior version or the new one,
            // never a partial write.
            File.Move(tempPath, StatePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersistedConciergeState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            PersistedConciergeState? loaded;
            try
            {
                loaded = await JsonSerializer.DeserializeAsync<PersistedConciergeState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return null;
            }

            if (loaded is null || loaded.SchemaVersion != CurrentSchemaVersion)
            {
                return null;
            }

            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryRestrictPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Permission tightening is best-effort; failing should not block persistence.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup failure should not mask the original exception.
        }
    }
}
