using System.Text.Json;

namespace Concierge.Shared.Settings;

/// <summary>Something to be done later, whether or not the app is open when it falls due.</summary>
/// <param name="Id">Stable identity.</param>
/// <param name="Description">What should happen.</param>
/// <param name="DueAt">When it is next due.</param>
/// <param name="Repeat">How often it recurs, or null for once only.</param>
/// <param name="LastRanAt">When it last ran, or null if it never has.</param>
public sealed record ScheduledTask(
    Guid Id,
    string Description,
    DateTimeOffset DueAt,
    TimeSpan? Repeat,
    DateTimeOffset? LastRanAt);

/// <summary>
/// Keeps what is due and what has run.
/// </summary>
/// <remarks>
/// Deliberately only the record, not the waking. What wakes the app differs per host — a
/// MAUI background task, a Windows service, or simply the user reopening it — but all of
/// them need the same durable answer to "what was I supposed to do?".
/// </remarks>
public interface IScheduledTaskStore
{
    /// <summary>Add something to be done later.</summary>
    ScheduledTask Schedule(string description, DateTimeOffset dueAt, TimeSpan? repeat = null);

    /// <summary>Everything scheduled, due or not.</summary>
    IReadOnlyList<ScheduledTask> All();

    /// <summary>Everything due at or before <paramref name="asOf"/>, oldest first.</summary>
    IReadOnlyList<ScheduledTask> Due(DateTimeOffset asOf);

    /// <summary>Record that a task ran, advancing it if it repeats.</summary>
    void MarkRan(Guid id, DateTimeOffset ranAt);

    /// <summary>Remove a task entirely.</summary>
    void Cancel(Guid id);
}

/// <summary>Scheduled tasks kept in a JSON file on the device.</summary>
public sealed class FileScheduledTaskStore : IScheduledTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;

    public FileScheduledTaskStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public ScheduledTask Schedule(string description, DateTimeOffset dueAt, TimeSpan? repeat = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var task = new ScheduledTask(Guid.NewGuid(), description.Trim(), dueAt, repeat, LastRanAt: null);
        lock (_gate)
        {
            var tasks = ReadUnlocked();
            tasks.Add(task);
            WriteUnlocked(tasks);
        }

        return task;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTask> All()
    {
        lock (_gate)
        {
            return ReadUnlocked();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTask> Due(DateTimeOffset asOf)
    {
        lock (_gate)
        {
            // A task whose time passed while the phone was off is still due. Skipping it
            // because the moment went by is how a reminder becomes untrustworthy.
            return ReadUnlocked()
                .Where(task => task.DueAt <= asOf)
                .OrderBy(task => task.DueAt)
                .ToList();
        }
    }

    /// <inheritdoc />
    public void MarkRan(Guid id, DateTimeOffset ranAt)
    {
        lock (_gate)
        {
            var tasks = ReadUnlocked();
            var index = tasks.FindIndex(task => task.Id == id);
            if (index < 0)
            {
                return;
            }

            var task = tasks[index];
            if (task.Repeat is { } repeat && repeat > TimeSpan.Zero)
            {
                // Advance from the time it ran, not from when it was due: a task that fired
                // three days late should next fire tomorrow, not catch up three times.
                tasks[index] = task with { LastRanAt = ranAt, DueAt = ranAt + repeat };
            }
            else
            {
                tasks[index] = task with { LastRanAt = ranAt, DueAt = DateTimeOffset.MaxValue };
            }

            WriteUnlocked(tasks);
        }
    }

    /// <inheritdoc />
    public void Cancel(Guid id)
    {
        lock (_gate)
        {
            var tasks = ReadUnlocked();
            if (tasks.RemoveAll(task => task.Id == id) > 0)
            {
                WriteUnlocked(tasks);
            }
        }
    }

    private List<ScheduledTask> ReadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ScheduledTask>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private void WriteUnlocked(List<ScheduledTask> tasks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(tasks, JsonOptions));
    }
}
