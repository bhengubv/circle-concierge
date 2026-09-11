using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// Work that runs end to end rather than one tool at a time.
///
/// OpenMontage's twelve pipelines are the thing being answered. A production is a dozen steps
/// in a fixed order, and doing them one call at a time is where a model loses its place,
/// repeats a step, or stops halfway and reports success — which is not a model problem to be
/// prompted away, it is an absence of anywhere to keep the place.
///
/// So the two things worth testing are the place being kept and the stopping: an interrupted
/// run carries on rather than starting again, and a failed step stops everything after it
/// instead of building on something that is not there.
/// </summary>
public sealed class ProductionRoutineTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "concierge-routines", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A tool that counts how often it was called and can be made to fail.</summary>
    private sealed class Counting(string name, bool works = true) : IAgentTool
    {
        public int Calls { get; private set; }

        public List<string> Saw { get; } = [];

        public string Name => name;
        public string Description => name;
        public JsonNode? ArgumentsSchema => new JsonObject { ["type"] = "object" };
        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            Calls++;
            Saw.Add(arguments?.ToJsonString() ?? string.Empty);

            return Task.FromResult(works
                ? new AgentToolResult(true, $"{name} done")
                : new AgentToolResult(false, string.Empty, $"{name} would not"));
        }
    }

    private sealed class Holding(params IAgentTool[] tools) : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> Tools => tools;

        public string BuildSystemPromptAddendum() => string.Empty;
    }

    private ProductionRoutines With(params IAgentTool[] tools)
        => new(() => new Holding(tools), _folder);

    private static ProductionRoutine Routine(params string[] steps)
        => new(
            "a routine",
            "Does a few things.",
            [],
            [.. steps.Select(step => new RoutineStep(step, new Dictionary<string, string>(), $"Do {step}"))]);

    // ── Running ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_step_runs_in_order()
    {
        var first = new Counting("one");
        var second = new Counting("two");

        var (progress, said) = await With(first, second)
            .RunAsync("run-1", Routine("one", "two"), new Dictionary<string, string>());

        Assert.Null(progress.Trouble);
        Assert.Equal(2, progress.Done);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal(2, said.Count);
    }

    /// <summary>
    /// A routine that carried on past a step that did not work would be building on something
    /// that is not there, which is how a production ends with a file nobody can explain.
    /// </summary>
    [Fact]
    public async Task A_step_that_does_not_work_stops_everything_after_it()
    {
        var broken = new Counting("two", works: false);
        var after = new Counting("three");

        var (progress, _) = await With(new Counting("one"), broken, after)
            .RunAsync("run-2", Routine("one", "two", "three"), new Dictionary<string, string>());

        Assert.NotNull(progress.Trouble);
        Assert.Equal(1, progress.Done);
        Assert.Equal(0, after.Calls);
    }

    /// <summary>
    /// A routine that reported only "failed" would leave nobody able to tell whether anything
    /// had been done to their design at all.
    /// </summary>
    [Fact]
    public async Task What_comes_back_says_what_was_done_before_it_stopped()
    {
        var (_, said) = await With(new Counting("one"), new Counting("two", works: false))
            .RunAsync("run-3", Routine("one", "two"), new Dictionary<string, string>());

        Assert.Contains(said, line => line.Contains("one done", StringComparison.Ordinal));
        Assert.Contains(said, line => line.Contains("would not", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_whose_tool_is_not_here_stops_rather_than_being_skipped()
    {
        var after = new Counting("three");

        var (progress, _) = await With(new Counting("one"), after)
            .RunAsync("run-4", Routine("one", "two", "three"), new Dictionary<string, string>());

        Assert.Contains("two", progress.Trouble!, StringComparison.Ordinal);
        Assert.Equal(0, after.Calls);
    }

    // ── Keeping its place ─────────────────────────────────────────────────

    /// <summary>
    /// A twenty-minute production interrupted by a closed app should not begin again.
    /// </summary>
    [Fact]
    public async Task An_interrupted_run_carries_on_where_it_stopped()
    {
        var first = new Counting("one");
        var second = new Counting("two", works: false);
        var routine = Routine("one", "two");

        await With(first, second).RunAsync("same-run", routine, new Dictionary<string, string>());

        Assert.Equal(1, first.Calls);

        // The second time, whatever stopped it is fixed and the first step must not run again.
        var mended = new Counting("two");
        var (progress, said) = await With(first, mended)
            .RunAsync("same-run", routine, new Dictionary<string, string>());

        Assert.Null(progress.Trouble);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, mended.Calls);
        Assert.Contains(said, line => line.Contains("Carrying on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_different_run_starts_from_the_beginning()
    {
        var first = new Counting("one");

        await With(first).RunAsync("one-run", Routine("one"), new Dictionary<string, string>());
        await With(first).RunAsync("another-run", Routine("one"), new Dictionary<string, string>());

        Assert.Equal(2, first.Calls);
    }

    /// <summary>
    /// Filling it in again on a second call would let a run change its own inputs halfway
    /// through, so the first half and the second half of one production disagree.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_keeps_what_it_was_given_the_first_time()
    {
        var routine = new ProductionRoutine(
            "a routine", "Does a thing.", [],
            [
                new("one", new Dictionary<string, string>(), "First"),
                new("two", new Dictionary<string, string> { ["path"] = "{file}" }, "Second"),
            ]);

        var stubborn = new Counting("two", works: false);
        await With(new Counting("one"), stubborn)
            .RunAsync("kept", routine, new Dictionary<string, string> { ["file"] = "first.mp4" });

        var mended = new Counting("two");
        await With(new Counting("one"), mended)
            .RunAsync("kept", routine, new Dictionary<string, string> { ["file"] = "second.mp4" });

        Assert.Contains("first.mp4", mended.Saw[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nowhere_to_keep_its_place_it_still_runs()
    {
        var first = new Counting("one");
        var routines = new ProductionRoutines(() => new Holding(first), keepIn: null);

        var (progress, _) = await routines.RunAsync("nowhere", Routine("one"), new Dictionary<string, string>());

        Assert.Null(progress.Trouble);
        Assert.Equal(1, first.Calls);
    }

    // ── Filling things in ─────────────────────────────────────────────────

    [Fact]
    public void What_was_given_is_put_into_the_arguments()
        => Assert.Equal(
            "clips/walk.mp4",
            ProductionRoutines.Fill("{folder}/walk.mp4", new Dictionary<string, string> { ["folder"] = "clips" }));

    /// <summary>
    /// `{shot}` handed to a tool as those six characters fails somewhere further in, saying
    /// "there is nothing called {shot}" — which sends whoever reads it looking at the canvas
    /// instead of at the routine.
    /// </summary>
    [Fact]
    public async Task A_blank_nobody_filled_in_stops_the_run_and_names_itself()
    {
        var routine = new ProductionRoutine(
            "a routine", "Does a thing.", [],
            [new("one", new Dictionary<string, string> { ["path"] = "{file}" }, "First")]);

        var never = new Counting("one");

        var (progress, _) = await With(never).RunAsync("blank", routine, new Dictionary<string, string>());

        Assert.Contains("{file}", progress.Trouble!, StringComparison.Ordinal);
        Assert.Equal(0, never.Calls);
    }

    // ── As tools ──────────────────────────────────────────────────────────

    private IAgentTool Tool(string name, params IAgentTool[] tools)
        => new RoutineToolSource(With(tools)).Tools.Single(tool => tool.Name == name);

    [Fact]
    public async Task Asked_what_routines_there_are_it_says_what_each_one_needs()
    {
        var result = await Tool("routines").InvokeAsync(new JsonObject());

        Assert.True(result.Success);
        Assert.Contains("look at a video", result.Output, StringComparison.Ordinal);
        Assert.Contains("Needs: file", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A routine that gets three steps in and then stops because nobody said which file has
    /// already changed somebody's design.
    /// </summary>
    [Fact]
    public async Task A_routine_missing_something_it_needs_is_refused_before_anything_runs()
    {
        var never = new Counting("media_facts");

        var result = await Tool("run_routine", never).InvokeAsync(new JsonObject
        {
            ["routine"] = "look at a video",
            ["run"] = "run-5",
        });

        Assert.False(result.Success);
        Assert.Contains("file", result.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(0, never.Calls);
    }

    [Fact]
    public async Task A_routine_nobody_has_is_refused_with_the_ones_there_are()
    {
        var result = await Tool("run_routine").InvokeAsync(new JsonObject
        {
            ["routine"] = "make a feature film",
            ["run"] = "run-6",
        });

        Assert.False(result.Success);
        Assert.Contains("look at a video", result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every step goes through the ordinary registry, so each tool keeps its own approval.
    /// Running a routine is a way of not losing count, never a way around anything.
    /// </summary>
    [Fact]
    public void Running_one_is_not_read_only_because_what_is_inside_it_is_not()
        => Assert.False(Tool("run_routine").IsReadOnly);

    [Fact]
    public void Looking_at_the_list_changes_nothing()
        => Assert.True(Tool("routines").IsReadOnly);

    [Fact]
    public void Every_routine_names_everything_its_steps_expect_to_be_filled_in()
    {
        foreach (var routine in ProductionRoutines.BuiltIn)
        {
            foreach (var step in routine.Steps)
            {
                foreach (var value in step.With.Values.Where(value => value.Contains('{')))
                {
                    var name = value[(value.IndexOf('{') + 1)..value.IndexOf('}')];

                    Assert.Contains(name, routine.Needs);
                }
            }
        }
    }
}
