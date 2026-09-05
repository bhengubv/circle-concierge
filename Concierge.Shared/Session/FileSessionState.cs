using System.Text.Json;

namespace Concierge.Shared.Session;

/// <summary>
/// Session state as one small JSON file beside the drafts.
///
/// It sits in the same place for the same reason: this is local-first, so what
/// you had is kept on your machine and goes nowhere. It is not in the SQLite
/// store because it is not conversation data — it is where you were standing,
/// and it should be discardable without touching anything you said.
///
/// Every operation swallows its own failures. A session file that cannot be
/// read is an empty session; one that cannot be written costs you your place
/// next launch. Neither is worth failing a start-up or a keystroke over.
/// </summary>
public sealed class FileSessionState : ISessionState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Guards against a save landing while another is mid-write.
    /// The composer saves as you type, so this is not theoretical.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _path;

    // System.IO.Path is qualified throughout: this class exposes a Path
    // property of its own — matching IConciergeSecretStore — which shadows it.
    public FileSessionState(string? path = null)
        => _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Concierge",
            "session.json");

    public string Path => _path;

    /// <summary>
    /// Reads synchronously and hands back a completed task, which is not the
    /// usual shape and is deliberate.
    ///
    /// This is awaited from OnInitializedAsync. An await that actually yields
    /// there leaves the component unfinished when the next interaction arrives
    /// — nineteen component tests failed exactly that way. The file is a few
    /// hundred bytes read once at start-up, so there is nothing to gain by
    /// going async and a render loop to lose.
    /// </summary>
    public Task<SessionSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Task.FromResult(SessionSnapshot.Empty);
            }

            var json = File.ReadAllText(_path);

            return Task.FromResult(string.IsNullOrWhiteSpace(json)
                ? SessionSnapshot.Empty
                : JsonSerializer.Deserialize<SessionSnapshot>(json, Json) ?? SessionSnapshot.Empty);
        }
        catch (Exception)
        {
            // A corrupt or unreadable file must not stop the app opening. An
            // empty session is exactly what a first launch looks like.
            return Task.FromResult(SessionSnapshot.Empty);
        }
    }

    public async Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside and moved into place: a crash halfway through a
            // write would otherwise leave a truncated file, and the next launch
            // would read it as an empty session and silently drop your skills.
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, Json), cancellationToken)
                      .ConfigureAwait(false);

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception)
        {
            // Losing your place is a smaller cost than losing the keystroke.
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception)
        {
            // Left behind; the next save overwrites it.
        }

        return Task.CompletedTask;
    }
}
