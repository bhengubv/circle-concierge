using System.Text.Json;

namespace Concierge.Shared.Settings;

/// <summary>
/// Settings read from a file that can change while the app is running.
/// </summary>
/// <remarks>
/// <para>
/// Restarting to pick up a setting costs the model load and the warm cache — several seconds
/// of a loading screen because someone moved a toggle. Re-reading a small file costs
/// nothing.
/// </para>
/// <para>
/// A file that will not parse leaves the last good values in place. Half-written JSON after
/// a crash must not silently reset a parent's Kid Mode choice to the default.
/// </para>
/// </remarks>
public sealed class HotReloadingSettings<T> where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<T> _fallback;
    private T _current;

    /// <param name="path">The settings file. It need not exist yet.</param>
    /// <param name="fallback">What to use when there is no readable file.</param>
    public HotReloadingSettings(string path, Func<T> fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fallback);

        _path = path;
        _fallback = fallback;

        // Loaded now, not lazily. Reload compares against what was previously in force, and
        // that comparison is only meaningful if something was read before the file changed.
        _current = ReadUnlocked() ?? fallback();
    }

    /// <summary>Raised after a reload that actually changed something.</summary>
    public event EventHandler? Changed;

    /// <summary>The settings in force now.</summary>
    public T Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Re-read the file, keeping the current values if it cannot be read.</summary>
    public void Reload()
    {
        bool changed;
        lock (_gate)
        {
            var loaded = ReadUnlocked();
            if (loaded is null)
            {
                // Unreadable. What is already loaded is known good; replacing it with
                // defaults would undo a choice the user made deliberately.
                return;
            }

            var previous = _current;
            _current = loaded;
            changed = !JsonEquals(previous, loaded);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private T? ReadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares by serialized form so any settings shape works without callers having to
    /// implement equality on their own types.
    /// </summary>
    private static bool JsonEquals(T left, T right)
        => string.Equals(
            JsonSerializer.Serialize(left, JsonOptions),
            JsonSerializer.Serialize(right, JsonOptions),
            StringComparison.Ordinal);
}
