using System.Text.Json;
using Concierge.Shared.Tools;

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
    DateTimeOffset? LastRanAt)
{
    public bool Enabled { get; init; } = true;
    public string State { get; init; } = "Scheduled";
    public Guid? ConversationId { get; init; }
    public Guid? RunId { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public ToolPermissionMode PermissionMode { get; init; } = ToolPermissionMode.AskFirst;
}

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
    ScheduledTask Schedule(string description, DateTimeOffset dueAt, TimeSpan? repeat = null, ToolPermissionMode permission = ToolPermissionMode.AskFirst);

    /// <summary>Everything scheduled, due or not.</summary>
    IReadOnlyList<ScheduledTask> All();

    /// <summary>Everything due at or before <paramref name="asOf"/>, oldest first.</summary>
    IReadOnlyList<ScheduledTask> Due(DateTimeOffset asOf);

    /// <summary>Record that a task ran, advancing it if it repeats.</summary>
    void MarkRan(Guid id, DateTimeOffset ranAt);

    /// <summary>Remove a task entirely.</summary>
    void Cancel(Guid id);
    ScheduledTask? Begin(Guid id, DateTimeOffset now);
    void Finish(Guid id, DateTimeOffset ranAt, string state, string? result, string? error);
    void SetEnabled(Guid id, bool enabled);
    void Update(Guid id, string description, DateTimeOffset dueAt, TimeSpan? repeat, ToolPermissionMode permission);
    void Retry(Guid id, DateTimeOffset now);
    IDisposable? TryAcquireExecutionLease();
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
    public ScheduledTask Schedule(string description, DateTimeOffset dueAt, TimeSpan? repeat = null, ToolPermissionMode permission = ToolPermissionMode.AskFirst)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Validate(description, repeat);
        if (!Enum.IsDefined(permission)) throw new ArgumentException("Choose a valid action mode.");

        var task = new ScheduledTask(Guid.NewGuid(), description.Trim(), dueAt, repeat, LastRanAt: null) { PermissionMode = permission };
        lock (_gate)
        {
            using var lease = LocalStateWriteLease.Acquire(_path);
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

    public IDisposable? TryAcquireExecutionLease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        try { return File.Open(_path + ".worker.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; } // Another Concierge process is already executing this device's schedule.
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTask> Due(DateTimeOffset asOf)
    {
        lock (_gate)
        {
            // A task whose time passed while the phone was off is still due. Skipping it
            // because the moment went by is how a reminder becomes untrustworthy.
            return ReadUnlocked()
                .Where(task => task.Enabled && task.DueAt <= asOf && task.State is "Scheduled" or "Running")
                .OrderBy(task => task.DueAt)
                .ToList();
        }
    }

    /// <inheritdoc />
    public void MarkRan(Guid id, DateTimeOffset ranAt)
    {
        lock (_gate)
        {
            using var lease = LocalStateWriteLease.Acquire(_path);
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
            using var lease = LocalStateWriteLease.Acquire(_path);
            var tasks = ReadUnlocked();
            if (tasks.RemoveAll(task => task.Id == id) > 0)
            {
                WriteUnlocked(tasks);
            }
        }
    }

    public ScheduledTask? Begin(Guid id, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var lease = LocalStateWriteLease.Acquire(_path);
            var tasks = ReadUnlocked();
            var index = tasks.FindIndex(t => t.Id == id);
            if (index < 0 || !tasks[index].Enabled || tasks[index].DueAt > now
                || tasks[index].State is not ("Scheduled" or "Running")) return null;
            var started = tasks[index] with { State = "Running", Error = null,
                ConversationId = tasks[index].ConversationId ?? Guid.NewGuid(), RunId = tasks[index].RunId ?? Guid.NewGuid() };
            tasks[index] = started;
            WriteUnlocked(tasks);
            return started;
        }
    }

    public void Finish(Guid id, DateTimeOffset ranAt, string state, string? result, string? error)
        => Change(id, task => task with
        {
            State = state == "Completed" && task.Repeat is not null ? "Scheduled" : state,
            LastRanAt = ranAt, Result = result is { Length: > 16_000 } ? result[..16_000] : result, Error = error,
            DueAt = state == "Completed" ? task.Repeat is { } repeat ? ranAt + repeat : DateTimeOffset.MaxValue : task.DueAt,
            RunId = state == "Completed" ? null : task.RunId,
        });

    public void SetEnabled(Guid id, bool enabled) => Change(id, task => task with { Enabled = enabled });

    public void Update(Guid id, string description, DateTimeOffset dueAt, TimeSpan? repeat, ToolPermissionMode permission)
    {
        Validate(description, repeat);
        if (!Enum.IsDefined(permission)) throw new ArgumentException("Choose a valid action mode.");
        Change(id, task => task.State == "Running" ? throw new InvalidOperationException("Stop this task before editing it.")
            : task with { Description = description.Trim(), DueAt = dueAt, Repeat = repeat, PermissionMode = permission,
                State = "Scheduled", RunId = null, Error = null });
    }

    public void Retry(Guid id, DateTimeOffset now) => Change(id, task => task.State == "Running"
        ? throw new InvalidOperationException("This task is already running.")
        : task with { DueAt = now, State = "Scheduled", Enabled = true, RunId = null, Error = null });

    private void Change(Guid id, Func<ScheduledTask, ScheduledTask> change)
    {
        lock (_gate)
        {
            using var lease = LocalStateWriteLease.Acquire(_path);
            var tasks = ReadUnlocked();
            var index = tasks.FindIndex(t => t.Id == id);
            if (index < 0) return;
            tasks[index] = change(tasks[index]);
            WriteUnlocked(tasks);
        }
    }

    private static void Validate(string description, TimeSpan? repeat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > 16_000) throw new ArgumentException("Keep scheduled instructions under 16,000 characters.");
        if (repeat is { } interval && interval < TimeSpan.FromMinutes(1))
            throw new ArgumentException("Repeating work needs at least a minute between runs.");
    }

    private List<ScheduledTask> ReadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<ScheduledTask>>(File.ReadAllText(_path), JsonOptions)
            ?? throw new InvalidDataException("The schedule file is damaged. It has not been replaced.");
    }

    private void WriteUnlocked(List<ScheduledTask> tasks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(tasks, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
