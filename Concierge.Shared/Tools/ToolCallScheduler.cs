using System.Text.Json.Nodes;

namespace Concierge.Shared.Tools;

/// <summary>One call the model asked for, ready to run.</summary>
/// <param name="ToolName">The tool named in the call.</param>
/// <param name="Arguments">The arguments as the model produced them.</param>
public sealed record PlannedToolCall(string ToolName, JsonNode? Arguments);

/// <summary>What became of one planned call.</summary>
public enum ToolCallDisposition
{
    /// <summary>The tool was invoked and returned or threw.</summary>
    Executed = 0,

    /// <summary>The call never ran, because the turn was abandoned first.</summary>
    Skipped = 1,
}

/// <summary>The record of one planned call, whatever happened to it.</summary>
/// <param name="ToolName">The tool named in the call.</param>
/// <param name="Result">What to show for it.</param>
/// <param name="Disposition">Whether it actually ran.</param>
public sealed record ToolCallOutcome(string ToolName, AgentToolResult Result, ToolCallDisposition Disposition);

/// <summary>
/// Runs the tool calls from one model step, overlapping the safe ones and serialising the
/// rest.
/// </summary>
/// <remarks>
/// Every planned call comes back with a result in the order the model asked, including calls
/// that were abandoned. A conversation reloaded after a cancelled turn must still show a
/// result for everything that was requested, or the history has a hole where the model
/// expects an answer.
/// </remarks>
public interface IToolCallScheduler
{
    /// <summary>Run one step's calls and report on every one of them.</summary>
    Task<IReadOnlyList<ToolCallOutcome>> ExecuteAsync(
        IReadOnlyList<PlannedToolCall> calls,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Groups consecutive concurrency-safe calls into a bounded pool and runs everything else
/// alone.
/// </summary>
/// <remarks>
/// <para>
/// Safety comes from <see cref="IAgentTool.IsConcurrencySafe"/>. A tool that only reads can
/// overlap freely; anything that writes a file, runs a process, or touches the model runs by
/// itself, because two of them at once is how a workspace gets corrupted.
/// </para>
/// <para>
/// The pool is bounded because the target device is a phone. Unbounded fan-out on four cores
/// and 2GB of RAM is slower than running in sequence, not faster.
/// </para>
/// </remarks>
public sealed class ToolCallScheduler : IToolCallScheduler
{
    private readonly IAgentToolRegistry _registry;
    private readonly int _maxParallel;

    /// <param name="hooks">
    /// Programs the person has registered to run in front of every tool call, or
    /// null for none — which is the default and what almost everybody has.
    ///
    /// This is where a hook earns its place: a preToolUse hook can refuse a call,
    /// which is a safety control somebody can write for themselves and is worth
    /// more than any list of rules shipped in the box, because they know what
    /// their machine holds and Concierge does not.
    /// </param>
    public ToolCallScheduler(
        IAgentToolRegistry registry,
        int maxParallel = 4,
        Concierge.Shared.Hooks.IHookBridge? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallel, 1);

        _registry = registry;
        _maxParallel = maxParallel;
        _hooks = hooks;
    }

    private readonly Concierge.Shared.Hooks.IHookBridge? _hooks;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolCallOutcome>> ExecuteAsync(
        IReadOnlyList<PlannedToolCall> calls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calls);

        var outcomes = new ToolCallOutcome?[calls.Count];
        var index = 0;

        while (index < calls.Count)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var group = NextGroup(calls, index);
            var running = new List<Task>(group.Count);

            for (var offset = 0; offset < group.Count; offset++)
            {
                var position = index + offset;
                running.Add(RunOneAsync(calls[position], outcomes, position, cancellationToken));
            }

            await Task.WhenAll(running).ConfigureAwait(false);
            index += group.Count;
        }

        // Anything the turn never reached still owes the conversation a result. Without
        // this the reloaded history shows a request the model never got an answer to.
        for (var position = 0; position < outcomes.Length; position++)
        {
            outcomes[position] ??= new ToolCallOutcome(
                calls[position].ToolName,
                new AgentToolResult(false, string.Empty, "This step was stopped before the call ran."),
                ToolCallDisposition.Skipped);
        }

        return outcomes.Select(outcome => outcome!).ToList();
    }

    /// <summary>
    /// The run of calls that may proceed together: consecutive concurrency-safe calls up to
    /// the pool limit, or exactly one call when the next is not safe to overlap.
    /// </summary>
    private List<PlannedToolCall> NextGroup(IReadOnlyList<PlannedToolCall> calls, int start)
    {
        var group = new List<PlannedToolCall> { calls[start] };
        if (!IsConcurrencySafe(calls[start].ToolName))
        {
            return group;
        }

        for (var position = start + 1; position < calls.Count && group.Count < _maxParallel; position++)
        {
            if (!IsConcurrencySafe(calls[position].ToolName))
            {
                break;
            }

            group.Add(calls[position]);
        }

        return group;
    }

    private bool IsConcurrencySafe(string toolName)
    {
        var tool = Find(toolName);

        // An unknown tool is treated as unsafe so it is resolved alone. Its call still
        // fails, but it never widens a pool on the strength of a name nobody recognises.
        return tool is not null && tool.IsReadOnly;
    }

    private IAgentTool? Find(string toolName)
        => _registry.Tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase));

    private async Task RunOneAsync(
        PlannedToolCall call,
        ToolCallOutcome?[] outcomes,
        int position,
        CancellationToken cancellationToken)
    {
        var tool = Find(call.ToolName);
        if (tool is null)
        {
            outcomes[position] = new ToolCallOutcome(
                call.ToolName,
                new AgentToolResult(false, string.Empty, $"Unknown tool '{call.ToolName}'."),
                ToolCallDisposition.Executed);
            return;
        }

        try
        {
            // Asked before the tool runs, and obeyed when it refuses. A hook that
            // fails or times out is no opinion rather than a refusal — a broken
            // script in somebody's configuration must not make the assistant
            // unusable — but one that says no is the whole reason to run it.
            if (_hooks is not null)
            {
                var payload = new System.Text.Json.Nodes.JsonObject
                {
                    ["tool_name"] = call.ToolName,
                    ["tool_input"] = call.Arguments?.DeepClone(),
                };

                var decision = await _hooks
                    .RunAsync(Concierge.Shared.Hooks.HookEvent.PreToolUse, payload, cancellationToken)
                    .ConfigureAwait(false);

                if (!decision.Allowed)
                {
                    outcomes[position] = new ToolCallOutcome(
                        call.ToolName,
                        new AgentToolResult(false, string.Empty,
                            decision.Reason ?? "A hook on this machine refused the call."),
                        ToolCallDisposition.Skipped);
                    return;
                }
            }

            var result = await tool.InvokeAsync(call.Arguments, cancellationToken).ConfigureAwait(false);
            outcomes[position] = new ToolCallOutcome(call.ToolName, result, ToolCallDisposition.Executed);
        }
        catch (OperationCanceledException)
        {
            outcomes[position] = new ToolCallOutcome(
                call.ToolName,
                new AgentToolResult(false, string.Empty, "This call was stopped part way through."),
                ToolCallDisposition.Skipped);
        }
        catch (Exception exception)
        {
            // A throwing tool is a failed call, never a failed step. One broken tool must
            // not take the other calls in the same step down with it.
            outcomes[position] = new ToolCallOutcome(
                call.ToolName,
                new AgentToolResult(false, string.Empty, $"Tool '{call.ToolName}' threw: {exception.Message}"),
                ToolCallDisposition.Executed);
        }
    }
}
