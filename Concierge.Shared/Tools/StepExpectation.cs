namespace Concierge.Shared.Tools;

/// <summary>What a step said it would leave behind.</summary>
public enum StepOutcome
{
    /// <summary>
    /// Nothing in particular. Any successful call counts, which is how every step
    /// behaved before expectations existed and how a step with none still behaves.
    /// </summary>
    Anything = 0,

    /// <summary>A named file is there afterwards.</summary>
    FileWritten = 1,

    /// <summary>A named file was read.</summary>
    FileRead = 2,

    /// <summary>A command ran and came back clean.</summary>
    CommandRan = 3,

    /// <summary>
    /// The design changed.
    ///
    /// Added when the canvas joined the agent loop. Without it a multi-step design
    /// change — "make it feel like a school newsletter", which is a look, a
    /// heading and three sizes — could state its steps but not what any of them
    /// would leave behind, so the strip fell back to ticking on any success. That
    /// is the exact behaviour `Judge` was written to stop, reintroduced through a
    /// medium it had never been taught about.
    /// </summary>
    CanvasChanged = 4,
}

/// <summary>
/// What actually happened during a round: one entry per call.
/// </summary>
/// <param name="Tool">The tool that was called.</param>
/// <param name="Target">
/// What it acted on — a path for a file tool, the command line for a shell call.
/// Null when the tool acts on nothing nameable.
/// </param>
/// <param name="Succeeded">Whether the call came back clean.</param>
public sealed record StepEvidence(string Tool, string? Target, bool Succeeded);

/// <summary>
/// What has to be true before a step may be ticked off.
///
/// <see cref="PlanProgress"/> ticked a step when *any* call in the round
/// succeeded. So a plan whose second step was "write the config file" ticked that
/// step when the model instead listed a directory successfully — the strip
/// advanced, the person watching believed the config had been written, and
/// nothing had been. That is the same defect as an approvals badge that always
/// said two, on the surface that exists to show what is happening.
///
/// OpenMontage validates each stage's output against a schema before the pipeline
/// may advance. The idea carries; the JSON schema does not. What a step here
/// promises is small and nameable — a file, a command — so the expectation is a
/// pair rather than a document, and the plan strip stays something a person can
/// read.
///
/// <see cref="StepOutcome.Anything"/> is the default on purpose. A step with no
/// stated expectation behaves exactly as every step did before, so adding this
/// cannot break a plan that does not use it.
/// </summary>
/// <param name="Expected">The kind of thing that has to have happened.</param>
/// <param name="Target">
/// What it has to have happened to. Matched loosely — a step says
/// <c>appsettings.json</c> and the call reports a full path, so containment is
/// the test rather than equality. Null means "anything of that kind will do".
/// </param>
public sealed record StepExpectation(StepOutcome Expected, string? Target = null)
{
    /// <summary>A step that promises nothing in particular.</summary>
    public static readonly StepExpectation Unstated = new(StepOutcome.Anything);

    /// <summary>
    /// Whether what happened satisfies what was promised.
    ///
    /// A failed call never satisfies anything, however well it matches: the point
    /// is what the step left behind, and a call that failed left nothing.
    /// </summary>
    public bool SatisfiedBy(IReadOnlyList<StepEvidence>? evidence)
    {
        var calls = (evidence ?? []).Where(call => call.Succeeded).ToList();

        if (calls.Count == 0)
        {
            return false;
        }

        if (Expected == StepOutcome.Anything)
        {
            return true;
        }

        return calls.Any(Matches);
    }

    private bool Matches(StepEvidence call)
    {
        if (!KindMatches(call.Tool))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Target))
        {
            return true;
        }

        // Loose on purpose. A step written by a model says "the config file" or
        // "appsettings.json"; the call reports whatever absolute path it actually
        // touched. Demanding equality would fail every real match and tick
        // nothing, which is worse than the problem being fixed.
        return call.Target is { } target
               && (target.Contains(Target, StringComparison.OrdinalIgnoreCase)
                   || Target.Contains(target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a tool is the kind of tool that produces this outcome.
    ///
    /// Matched on the name rather than a registry lookup, because the plan is
    /// judged from what the round reported and a step should not be able to fail
    /// because a tool was renamed somewhere else.
    /// </summary>
    private bool KindMatches(string tool) => Expected switch
    {
        StepOutcome.FileWritten =>
            tool.Contains("write", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("edit", StringComparison.OrdinalIgnoreCase),

        StepOutcome.FileRead =>
            tool.Contains("read", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("search", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("list", StringComparison.OrdinalIgnoreCase),

        StepOutcome.CommandRan =>
            tool.Contains("run", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("shell", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("command", StringComparison.OrdinalIgnoreCase),

        // Every design tool is prefixed, and describing is excluded on purpose:
        // it is the read-only one, and a step promising to change the design is
        // not kept by looking at it. That is the same distinction as a promise to
        // write a file not being kept by reading one.
        StepOutcome.CanvasChanged =>
            tool.StartsWith("design_", StringComparison.OrdinalIgnoreCase)
            && !tool.Equals("design_describe", StringComparison.OrdinalIgnoreCase),

        _ => true,
    };
}
