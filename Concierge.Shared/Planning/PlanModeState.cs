using Concierge.Shared.Tools;

namespace Concierge.Shared.Planning;

/// <summary>
/// Whether this session is planning rather than doing, held as state that constrains the
/// session's permissions.
/// </summary>
/// <remarks>
/// <para>
/// The distinction that matters: plan mode is not a sentence in the system prompt asking the
/// model to hold off. It narrows the session to read-only, so a model that ignores the
/// instruction still cannot change anything. A rule the model can disregard is not a mode.
/// </para>
/// <para>
/// Leaving requires a plan. That is what plan mode is for, and an exit with nothing to show
/// means the session spent its turns and produced no artifact.
/// </para>
/// </remarks>
public sealed class PlanModeState
{
    /// <summary>Whether the session is currently planning.</summary>
    public bool IsPlanning { get; private set; }

    /// <summary>The plan produced on the way out, or null if none has been submitted.</summary>
    public string? Plan { get; private set; }

    /// <summary>Enter plan mode, discarding any plan from a previous round.</summary>
    public void Enter()
    {
        IsPlanning = true;
        Plan = null;
    }

    /// <summary>Leave plan mode by submitting the plan.</summary>
    /// <exception cref="InvalidOperationException">The session was not planning.</exception>
    /// <exception cref="ArgumentException">The plan is empty.</exception>
    public void Exit(string plan)
    {
        if (!IsPlanning)
        {
            throw new InvalidOperationException("The session is not in plan mode.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(plan);

        Plan = plan.Trim();
        IsPlanning = false;
    }

    /// <summary>
    /// Narrow a session's permissions to what planning allows. While planning the answer is
    /// always read-only, whatever the session was otherwise granted.
    /// </summary>
    public ConciergePermissionPolicy Constrain(ConciergePermissionPolicy requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return IsPlanning
            ? ConciergePermissionPolicy.For(ConciergePermissionPreset.ReadOnly)
            : requested;
    }
}
