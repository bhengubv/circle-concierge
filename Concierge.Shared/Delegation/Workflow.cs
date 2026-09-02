namespace Concierge.Shared.Delegation;

/// <summary>What a step can see while it runs.</summary>
/// <param name="Previous">What the step before it produced, or empty for the first step.</param>
/// <param name="Results">Everything produced so far, by step name.</param>
public sealed record WorkflowContext(string Previous, IReadOnlyDictionary<string, string> Results)
{
    /// <summary>What a named earlier step produced, or null if it has not run.</summary>
    public string? ResultOf(string stepName)
        => Results.TryGetValue(stepName, out var result) ? result : null;
}

/// <summary>What a whole workflow produced.</summary>
/// <param name="Success">Whether every step ran.</param>
/// <param name="Output">What the last step produced.</param>
/// <param name="Steps">What each step that ran produced, by name.</param>
/// <param name="Error">Which step failed and why, when one did.</param>
public sealed record WorkflowResult(
    bool Success,
    string Output,
    IReadOnlyDictionary<string, string> Steps,
    string? Error);

/// <summary>
/// A fixed sequence of steps, run in order, each able to see what came before.
/// </summary>
/// <remarks>
/// <para>
/// The order lives in code rather than in the model's head. A capable model can improvise a
/// five-step job; a 4B model on a phone loses its place around step three and starts again.
/// Putting the sequence here leaves the model doing one well-defined thing at a time, which
/// is the size of job it is actually good at.
/// </para>
/// <para>
/// A failure stops the run and keeps what earlier steps produced. Half a workflow's output
/// is still useful, and it is usually what shows where things went wrong.
/// </para>
/// </remarks>
public sealed class Workflow
{
    private readonly List<(string Name, Func<WorkflowContext, Task<string>> Work)> _steps = [];

    /// <param name="name">What this workflow is for.</param>
    public Workflow(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>What this workflow is for.</summary>
    public string Name { get; }

    /// <summary>Add a step to the end.</summary>
    /// <exception cref="ArgumentException">The name is blank or already used.</exception>
    public Workflow Then(string stepName, Func<WorkflowContext, Task<string>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(work);

        // Results are looked up by name, so a duplicate would shadow the earlier step's
        // output and produce a workflow that is wrong rather than one that fails.
        if (_steps.Any(step => string.Equals(step.Name, stepName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Step '{stepName}' is already in this workflow.", nameof(stepName));
        }

        _steps.Add((stepName.Trim(), work));
        return this;
    }

    /// <summary>Run every step in order.</summary>
    public async Task<WorkflowResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = string.Empty;

        foreach (var (stepName, work) in _steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new WorkflowResult(false, previous, results, "The workflow was stopped.");
            }

            try
            {
                previous = await work(new WorkflowContext(previous, results)).ConfigureAwait(false) ?? string.Empty;
                results[stepName] = previous;
            }
            catch (OperationCanceledException)
            {
                return new WorkflowResult(false, previous, results, $"Step '{stepName}' was stopped.");
            }
            catch (Exception exception)
            {
                // Naming the step is the whole value of the error: a workflow that reports
                // only "it broke" leaves you re-running it to find out where.
                return new WorkflowResult(false, previous, results, $"Step '{stepName}' failed: {exception.Message}");
            }
        }

        return new WorkflowResult(true, previous, results, null);
    }
}
