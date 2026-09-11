using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Tools;

/// <summary>One step of a routine: a tool, and what to call it with.</summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="With">Its arguments. A value of <c>{something}</c> is filled in at the start.</param>
/// <param name="Says">What to say while it runs, in plain words.</param>
public sealed record RoutineStep(string Tool, IReadOnlyDictionary<string, string> With, string Says);

/// <summary>Something that runs end to end.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="What">What it does, in one line.</param>
/// <param name="Needs">What has to be filled in before it can run.</param>
/// <param name="Steps">What it does, in order.</param>
public sealed record ProductionRoutine(
    string Name, string What, IReadOnlyList<string> Needs, IReadOnlyList<RoutineStep> Steps);

/// <summary>Where a run got to.</summary>
/// <param name="Routine">Which routine.</param>
/// <param name="Done">How many steps finished.</param>
/// <param name="Total">How many there are.</param>
/// <param name="Filled">What it was given to fill in, kept so a resumed run is the same run.</param>
/// <param name="Trouble">What stopped it, when something did.</param>
public sealed record RoutineProgress(
    string Routine, int Done, int Total, IReadOnlyDictionary<string, string> Filled, string? Trouble);

/// <summary>
/// Work that runs end to end rather than one tool at a time.
///
/// OpenMontage's twelve pipelines are the thing being answered: a production is a dozen
/// steps in a fixed order, and doing them one call at a time is where a model loses its
/// place, repeats a step, or stops halfway and reports success. That is not a model problem
/// to be prompted away — it is an absence of anywhere to keep the place.
///
/// **The place is kept on disk, so an interrupted run resumes rather than restarts.** That is
/// the half of OpenMontage's checkpointing that matters here: a twenty-minute production
/// interrupted by a closed app should not begin again, and a person should be able to see how
/// far it got.
///
/// **Every step goes through the ordinary tool registry**, which means every approval still
/// happens: a step that fetches a track still asks, a step that runs a command still asks.
/// Running a routine is not a way around anything — it is a way of not losing count.
///
/// **It stops at the first failure.** A routine that carried on past a step that did not work
/// would be building on something that is not there, which is how a production ends with a
/// file nobody can explain.
/// </summary>
public sealed class ProductionRoutines
{
    private readonly Func<IAgentToolRegistry> _tools;
    private readonly string? _keepIn;

    /// <param name="tools">
    /// Where the steps are actually run, asked for when a run starts rather than held.
    ///
    /// A function rather than the registry itself, and not for neatness: the registry is
    /// built from every tool source, this publishes two of them, and holding the registry
    /// here would be a circle the container cannot resolve. Asking late also means a routine
    /// sees the tools that exist *now* — the design tools appear when a canvas opens.
    /// </param>
    /// <param name="keepIn">
    /// Where a run's place is kept. Null on a head with nowhere to write, and a run there is
    /// simply not resumable — it still runs.
    /// </param>
    public ProductionRoutines(Func<IAgentToolRegistry> tools, string? keepIn = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _keepIn = keepIn;
    }

    /// <summary>The routines it knows.</summary>
    public IReadOnlyList<ProductionRoutine> All => BuiltIn;

    public ProductionRoutine? Find(string name)
        => All.FirstOrDefault(routine =>
            routine.Name.Equals(name?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where this run's place is kept, or null when nowhere is.</summary>
    private string? PlaceOf(string runId)
        => string.IsNullOrWhiteSpace(_keepIn) ? null : Path.Combine(_keepIn, $"{runId}.json");

    /// <summary>How far a run got, or null when there is no record of it.</summary>
    public RoutineProgress? Where(string runId)
    {
        var place = PlaceOf(runId);

        if (place is null || !File.Exists(place))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RoutineProgress>(File.ReadAllText(place));
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
    /// Runs a routine, from the start or from where it stopped.
    ///
    /// What comes back is what happened, step by step, including the step that did not work
    /// — a routine that reported only "failed" would leave nobody able to tell whether
    /// anything had been done to their design at all.
    /// </summary>
    public async Task<(RoutineProgress Progress, IReadOnlyList<string> Said)> RunAsync(
        string runId,
        ProductionRoutine routine,
        IReadOnlyDictionary<string, string> filling,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(filling);

        var before = Where(runId);

        // A resumed run keeps what it was given first time. Filling it in again from a
        // second call would let a run change its own inputs halfway through, which makes the
        // first half and the second half of one production disagree.
        var filled = before?.Filled ?? filling;
        var from = before is { Trouble: not null } ? before.Done : before?.Done ?? 0;

        var said = new List<string>();

        if (from > 0)
        {
            said.Add($"Carrying on from step {from + 1} of {routine.Steps.Count}.");
        }

        for (var at = from; at < routine.Steps.Count; at++)
        {
            var step = routine.Steps[at];
            var tool = _tools().Tools.FirstOrDefault(known =>
                known.Name.Equals(step.Tool, StringComparison.Ordinal));

            if (tool is null)
            {
                return Stop(runId, routine, at, filled, said,
                    $"Step {at + 1} needs {step.Tool}, which is not available here.");
            }

            var arguments = new JsonObject();

            foreach (var (key, value) in step.With)
            {
                var written = Fill(value, filled);

                // A blank left unfilled is refused rather than passed on. `{shot}` handed to
                // a tool as those six characters fails somewhere further in, saying "there is
                // nothing called {shot}", which sends whoever reads it looking at the canvas
                // instead of at the routine.
                if (written.Contains('{') && written.Contains('}'))
                {
                    return Stop(runId, routine, at, filled, said,
                        $"Step {at + 1} ({step.Says}) needs {written} filled in, and nothing was.");
                }

                arguments[key] = written;
            }

            var result = await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                // Stopped here, and the place is kept at this step rather than the next, so
                // resuming tries the step that failed instead of skipping it.
                return Stop(runId, routine, at, filled, said,
                    $"Step {at + 1} ({step.Says}) did not work: {result.FailureMessage}");
            }

            said.Add($"{step.Says} — {result.Output}");
            Keep(runId, new RoutineProgress(routine.Name, at + 1, routine.Steps.Count, filled, null));
        }

        return (new RoutineProgress(routine.Name, routine.Steps.Count, routine.Steps.Count, filled, null), said);
    }

    private (RoutineProgress, IReadOnlyList<string>) Stop(
        string runId,
        ProductionRoutine routine,
        int at,
        IReadOnlyDictionary<string, string> filled,
        List<string> said,
        string trouble)
    {
        var progress = new RoutineProgress(routine.Name, at, routine.Steps.Count, filled, trouble);

        Keep(runId, progress);
        said.Add(trouble);

        return (progress, said);
    }

    private void Keep(string runId, RoutineProgress progress)
    {
        var place = PlaceOf(runId);

        if (place is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(place)!);

            // Written beside and moved into place, the way the design file is: a write
            // interrupted by the app closing would otherwise leave half a file where the
            // record of the run used to be, and the whole point of it is surviving that.
            var beside = place + ".writing";

            File.WriteAllText(beside, JsonSerializer.Serialize(progress));
            File.Move(beside, place, overwrite: true);
        }
        catch (IOException)
        {
            // Losing the place costs resumability, not the run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A value with whatever was filled in put into it. `{clip}` becomes what the run was
    /// given for `clip`; anything nobody filled in is left exactly as it was written, because
    /// silently emptying it would hand a tool an argument that looks deliberate and is not.
    /// </summary>
    internal static string Fill(string value, IReadOnlyDictionary<string, string> filled)
    {
        foreach (var (key, with) in filled)
        {
            value = value.Replace("{" + key + "}", with, StringComparison.Ordinal);
        }

        return value;
    }

    /// <summary>
    /// The ones that ship.
    ///
    /// Short on purpose. A routine earns its place by being a sequence somebody genuinely
    /// repeats, and a catalogue of twelve that nobody runs is worse than three that people
    /// do — it is the same argument as six looks rather than a colour picker.
    /// </summary>
    public static readonly IReadOnlyList<ProductionRoutine> BuiltIn =
    [
        new(
            "look at a video",
            "Read what is in a video file, whether it has sound, and what the shots look like.",
            ["file"],
            [
                new("media_facts", new Dictionary<string, string> { ["path"] = "{file}" },
                    "Read how long it is and what is in it"),
                new("media_is_silent", new Dictionary<string, string> { ["path"] = "{file}" },
                    "Check whether there is anything to hear"),
                new("media_frames", new Dictionary<string, string> { ["path"] = "{file}", ["frames"] = "6" },
                    "Take frames from across it to look at"),
            ]),

        new(
            "make a sound into a file",
            "Turn what is on the canvas into an audio file somebody can keep.",
            ["name"],
            [
                new("design_making", new Dictionary<string, string> { ["making"] = "sound" },
                    "Make it a running order"),
                new("design_describe", new Dictionary<string, string>(),
                    "Look at what is actually on it"),
                new("design_save", new Dictionary<string, string> { ["name"] = "{name}" },
                    "Save it as a file"),
            ]),

        new(
            "finish a design",
            "Look at what is on the canvas, check it against the guide for what it is, and save it.",
            ["making", "name"],
            [
                new("design_guide", new Dictionary<string, string> { ["about"] = "{making}" },
                    "Read the guide for what this is"),
                new("design_describe", new Dictionary<string, string>(),
                    "Look at what is actually on the canvas"),
                new("design_save", new Dictionary<string, string> { ["name"] = "{name}" },
                    "Save it as a file"),
            ]),
    ];
}

/// <summary>Routines, as things a model can do.</summary>
public sealed class RoutineToolSource : IAgentToolSource
{
    private readonly ProductionRoutines _routines;

    public RoutineToolSource(ProductionRoutines routines)
        => _routines = routines ?? throw new ArgumentNullException(nameof(routines));

    /// <inheritdoc />
    public IReadOnlyList<IAgentTool> Tools => [new WhatRoutinesThereAre(_routines), new RunOne(_routines)];

    private sealed class WhatRoutinesThereAre(ProductionRoutines routines) : IAgentTool
    {
        public string Name => "routines";

        public string Description =>
            "List the routines that run end to end, and what each one needs to be told.";

        public JsonNode? ArgumentsSchema => new JsonObject { ["type"] = "object" };

        public bool IsReadOnly => true;

        public Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var lines = routines.All.Select(routine =>
                $"{routine.Name} — {routine.What} Needs: {string.Join(", ", routine.Needs)}.");

            return Task.FromResult(new AgentToolResult(
                true, "There are routines for:" + Environment.NewLine + string.Join(Environment.NewLine, lines)));
        }
    }

    private sealed class RunOne(ProductionRoutines routines) : IAgentTool
    {
        public string Name => "run_routine";

        public string Description =>
            "Run a routine end to end rather than one tool at a time. It stops at the first "
            + "step that does not work, and running it again carries on from there.";

        public JsonNode? ArgumentsSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["routine"] = new JsonObject { ["type"] = "string", ["description"] = "Which one." },
                ["run"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        "A name for this run. Use the same one again to carry on where it stopped.",
                },
                ["with"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "What to fill in — see what the routine needs.",
                },
            },
            ["required"] = new JsonArray("routine", "run"),
        };

        /// <summary>
        /// Not read-only: the steps inside it are not. Each of them keeps its own approval, so
        /// this is a way of not losing count rather than a way around anything.
        /// </summary>
        public bool IsReadOnly => false;

        public async Task<AgentToolResult> InvokeAsync(
            JsonNode? arguments, CancellationToken cancellationToken = default)
        {
            var name = Said(arguments, "routine");
            var run = Said(arguments, "run");

            if (name.Length == 0 || run.Length == 0)
            {
                return new AgentToolResult(false, string.Empty, "Say which routine, and a name for the run.");
            }

            if (routines.Find(name) is not { } routine)
            {
                return new AgentToolResult(
                    false,
                    string.Empty,
                    $"There is no routine called {name}. There is: "
                    + string.Join(", ", routines.All.Select(known => known.Name)) + ".");
            }

            var filling = new Dictionary<string, string>(StringComparer.Ordinal);

            if (arguments?["with"] is JsonObject given)
            {
                foreach (var (key, value) in given)
                {
                    filling[key] = value?.ToString() ?? string.Empty;
                }
            }

            // Checked before anything runs. A routine that gets three steps in and then stops
            // because nobody said which file has already changed somebody's design.
            var missing = routine.Needs.Where(need => !filling.ContainsKey(need)).ToList();

            if (missing.Count > 0)
            {
                return new AgentToolResult(
                    false, string.Empty, $"That routine still needs: {string.Join(", ", missing)}.");
            }

            var (progress, said) = await routines
                .RunAsync(run, routine, filling, cancellationToken)
                .ConfigureAwait(false);

            var report = string.Join(Environment.NewLine, said);

            return progress.Trouble is null
                ? new AgentToolResult(
                    true, $"{routine.Name}, all {progress.Total} steps.{Environment.NewLine}{report}")
                : new AgentToolResult(
                    false,
                    string.Empty,
                    $"{routine.Name} stopped at step {progress.Done + 1} of {progress.Total}. "
                    + $"Run it again with the same name to carry on.{Environment.NewLine}{report}");
        }

        private static string Said(JsonNode? arguments, string key)
            => arguments?[key] is JsonValue value && value.TryGetValue<string>(out var written)
                ? written.Trim()
                : string.Empty;
    }
}

/// <summary>Registering them.</summary>
public static class RoutineRegistration
{
    public static IServiceCollection AddConciergeRoutines(this IServiceCollection services, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp => new ProductionRoutines(
            sp.GetRequiredService<IAgentToolRegistry>,
            string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "runs")));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, RoutineToolSource>());

        return services;
    }
}
