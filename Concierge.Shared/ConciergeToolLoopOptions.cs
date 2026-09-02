namespace Concierge.Shared;

/// <summary>
/// The budgets the tool loop runs under.
/// </summary>
/// <remarks>
/// These were constants nailed into the chat page. They vary by device — a phone on battery
/// and a desktop on mains do not want the same ceilings — and Concierge already varies the
/// model by device state, so these belong in the same place.
/// </remarks>
public sealed class ConciergeToolLoopOptions
{
    /// <summary>
    /// How many rounds of tool calling one message may take before the loop stops.
    /// </summary>
    /// <remarks>
    /// Five is right for a small model on a phone, which usually goes in circles before it
    /// gets anywhere. A cloud model on a desktop can usefully take more.
    /// </remarks>
    public int MaxToolIterations { get; set; } = 5;

    /// <summary>
    /// The model's usable context, in tokens, used to decide when to compact.
    /// </summary>
    /// <remarks>
    /// Default sized for the smallest on-device model, because that is the one that hits the
    /// wall. A host that knows it is running something larger raises it.
    /// </remarks>
    public int ContextBudgetTokens { get; set; } = 4_096;

    /// <summary>Characters of tool output kept before the middle is trimmed.</summary>
    public int ToolResultThresholdChars { get; set; } = 8_192;

    /// <summary>How long any one tool call may run.</summary>
    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
