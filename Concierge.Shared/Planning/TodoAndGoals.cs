using System.Text.Json;

namespace Concierge.Shared.Planning;

/// <summary>Where one task has got to.</summary>
public enum TodoStatus
{
    /// <summary>Not started.</summary>
    Pending = 0,

    /// <summary>Being worked on now. More than one may be in progress.</summary>
    InProgress = 1,

    /// <summary>Finished.</summary>
    Completed = 2,
}

/// <summary>One line of the model's working list.</summary>
/// <param name="Content">What the task is, in a short imperative line.</param>
/// <param name="Status">Where it has got to.</param>
/// <remarks>
/// Deliberately without an id or a priority. The list is replaced whole on every write, so
/// entries need no stable identity and the model never has to reason about merging.
/// </remarks>
public sealed record TodoItem(string Content, TodoStatus Status);

/// <summary>
/// The model's working list for a job that spans more than one turn.
/// </summary>
public interface ITodoStore
{
    /// <summary>The current list, in the order it was written.</summary>
    IReadOnlyList<TodoItem> Read();

    /// <summary>Replace the whole list. Last write wins.</summary>
    void Write(IReadOnlyList<TodoItem> items);
}

/// <summary>A todo list kept in a JSON file.</summary>
/// <remarks>
/// A corrupt file reads as empty rather than throwing: a half-written list after a crash is
/// a small loss, and refusing to start the app over it is a large one.
/// </remarks>
public sealed class FileTodoStore : ITodoStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;

    public FileTodoStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public IReadOnlyList<TodoItem> Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(_path), JsonOptions) ?? [];
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
    }

    /// <inheritdoc />
    public void Write(IReadOnlyList<TodoItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(items, JsonOptions));
        }
    }
}

/// <summary>Something the user wants done, which outlives the conversation that raised it.</summary>
/// <param name="Id">Stable identity, because a goal is referred to again later.</param>
/// <param name="Description">What the user wants.</param>
/// <param name="CreatedAt">When it was raised.</param>
/// <param name="CompletedAt">When it was finished, or null while it is still open.</param>
public sealed record Goal(Guid Id, string Description, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    /// <summary>Whether this goal has been finished.</summary>
    public bool IsComplete => CompletedAt is not null;
}

/// <summary>Goals that survive the conversation and the process.</summary>
public interface IGoalStore
{
    /// <summary>Raise a new goal.</summary>
    Goal Add(string description);

    /// <summary>Mark a goal finished. Unknown ids are ignored.</summary>
    void Complete(Guid id);

    /// <summary>Goals not yet finished, oldest first.</summary>
    IReadOnlyList<Goal> ReadOpen();

    /// <summary>Every goal, finished or not, oldest first.</summary>
    IReadOnlyList<Goal> ReadAll();
}

/// <summary>Goals kept in a JSON file.</summary>
public sealed class FileGoalStore : IGoalStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;

    public FileGoalStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <inheritdoc />
    public Goal Add(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var goal = new Goal(Guid.NewGuid(), description.Trim(), DateTimeOffset.UtcNow, CompletedAt: null);
        lock (_gate)
        {
            var goals = ReadUnlocked();
            goals.Add(goal);
            WriteUnlocked(goals);
        }

        return goal;
    }

    /// <inheritdoc />
    public void Complete(Guid id)
    {
        lock (_gate)
        {
            var goals = ReadUnlocked();
            var index = goals.FindIndex(goal => goal.Id == id);
            if (index < 0)
            {
                return;
            }

            goals[index] = goals[index] with { CompletedAt = DateTimeOffset.UtcNow };
            WriteUnlocked(goals);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Goal> ReadOpen()
    {
        lock (_gate)
        {
            return ReadUnlocked().Where(goal => !goal.IsComplete).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Goal> ReadAll()
    {
        lock (_gate)
        {
            return ReadUnlocked();
        }
    }

    private List<Goal> ReadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Goal>>(File.ReadAllText(_path), JsonOptions) ?? [];
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

    private void WriteUnlocked(List<Goal> goals)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(goals, JsonOptions));
    }
}
