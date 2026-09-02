using Concierge.Shared.Planning;

namespace Concierge.Tests;

/// <summary>
/// What the todo list must do (parity feature 21): let the model keep its place in a job
/// that takes more than one turn.
/// </summary>
/// <remarks>
/// The list is replaced wholesale on every write — last write wins — so entries need no
/// identity and the model never has to reason about merging. A weak model forgets what it
/// was doing; this is where it writes that down.
/// </remarks>
public sealed class TodoStoreTests : IDisposable
{
    private readonly string _root;

    public TodoStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"concierge-todo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory.
        }
    }

    [Fact]
    public void A_new_list_is_empty()
    {
        Assert.Empty(NewStore().Read());
    }

    [Fact]
    public void What_is_written_can_be_read_back()
    {
        var store = NewStore();

        store.Write([new TodoItem("wire the approval seam", TodoStatus.InProgress)]);

        var item = Assert.Single(store.Read());
        Assert.Equal("wire the approval seam", item.Content);
        Assert.Equal(TodoStatus.InProgress, item.Status);
    }

    [Fact]
    public void A_second_write_replaces_the_first()
    {
        var store = NewStore();

        store.Write([new TodoItem("first", TodoStatus.Pending)]);
        store.Write([new TodoItem("second", TodoStatus.Pending)]);

        Assert.Equal("second", Assert.Single(store.Read()).Content);
    }

    [Fact]
    public void Writing_an_empty_list_clears_it()
    {
        var store = NewStore();
        store.Write([new TodoItem("something", TodoStatus.Pending)]);

        store.Write([]);

        Assert.Empty(store.Read());
    }

    [Fact]
    public void The_list_survives_a_restart()
    {
        var path = Path.Combine(_root, "todo.json");
        new FileTodoStore(path).Write([new TodoItem("survive", TodoStatus.Completed)]);

        var reopened = new FileTodoStore(path).Read();

        Assert.Equal("survive", Assert.Single(reopened).Content);
    }

    [Fact]
    public void Several_items_keep_the_order_they_were_written_in()
    {
        var store = NewStore();

        store.Write([
            new TodoItem("first", TodoStatus.Completed),
            new TodoItem("second", TodoStatus.InProgress),
            new TodoItem("third", TodoStatus.Pending),
        ]);

        Assert.Equal(["first", "second", "third"], store.Read().Select(item => item.Content));
    }

    [Fact]
    public void A_corrupt_file_reads_as_empty_rather_than_throwing()
    {
        // A half-written file after a crash must not stop the app from starting.
        var path = Path.Combine(_root, "todo.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Empty(new FileTodoStore(path).Read());
    }

    private FileTodoStore NewStore() => new(Path.Combine(_root, $"{Guid.NewGuid():N}.json"));
}

/// <summary>
/// What goals must do (parity feature 22): outlive the conversation that created them.
/// </summary>
/// <remarks>
/// A todo tracks the steps of one job. A goal is the job itself, and it is still there
/// tomorrow when the app is reopened and the conversation is gone.
/// </remarks>
public sealed class GoalStoreTests : IDisposable
{
    private readonly string _root;

    public GoalStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"concierge-goal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory.
        }
    }

    [Fact]
    public void A_new_store_has_no_goals()
    {
        Assert.Empty(NewStore().ReadOpen());
    }

    [Fact]
    public void An_added_goal_is_open()
    {
        var store = NewStore();

        store.Add("get the kids' homework done");

        Assert.Equal("get the kids' homework done", Assert.Single(store.ReadOpen()).Description);
    }

    [Fact]
    public void A_completed_goal_is_no_longer_open()
    {
        var store = NewStore();
        var goal = store.Add("finish it");

        store.Complete(goal.Id);

        Assert.Empty(store.ReadOpen());
    }

    [Fact]
    public void A_completed_goal_is_still_recorded()
    {
        var store = NewStore();
        var goal = store.Add("finish it");

        store.Complete(goal.Id);

        Assert.True(Assert.Single(store.ReadAll()).IsComplete);
    }

    [Fact]
    public void Completing_a_goal_that_does_not_exist_is_harmless()
    {
        var store = NewStore();

        store.Complete(Guid.NewGuid());

        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void Goals_survive_a_restart()
    {
        var path = Path.Combine(_root, "goals.json");
        new FileGoalStore(path).Add("still here tomorrow");

        Assert.Single(new FileGoalStore(path).ReadOpen());
    }

    [Fact]
    public void A_completion_survives_a_restart()
    {
        var path = Path.Combine(_root, "goals.json");
        var goal = new FileGoalStore(path).Add("done today");
        new FileGoalStore(path).Complete(goal.Id);

        Assert.Empty(new FileGoalStore(path).ReadOpen());
    }

    [Fact]
    public void An_empty_description_is_refused()
    {
        Assert.Throws<ArgumentException>(() => NewStore().Add("  "));
    }

    private FileGoalStore NewStore() => new(Path.Combine(_root, $"{Guid.NewGuid():N}.json"));
}
