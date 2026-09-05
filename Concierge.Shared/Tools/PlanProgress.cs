namespace Concierge.Shared.Tools;

/// <summary>What a round of tool calls means for the plan.</summary>
/// <param name="StepCompleted">Whether the plan strip should tick forward.</param>
/// <param name="AskForRevision">Whether the model should be told to rethink.</param>
/// <param name="ShouldStop">Whether to give up rather than keep spending rounds.</param>
/// <param name="Note">What to tell the model, or null.</param>
public sealed record RoundVerdict(bool StepCompleted, bool AskForRevision, bool ShouldStop, string? Note);

/// <summary>
/// Keeping the stated plan honest about what actually happened.
///
/// Concierge stated a plan and then ticked one step off per round of tool calls
/// regardless of whether the round worked. A run where every call failed still
/// showed "3 of 5" — the strip asserting progress nobody had made, which is the
/// same fault as an approvals badge that always said two, in the place a person
/// looks precisely because they are deciding whether to let it carry on.
///
/// It also never asked for a rethink. A failed step left the rest of the plan
/// standing on an assumption that was no longer true, and the loop ran on until
/// it hit its round cap with no explanation. OpenDroid's agent loop re-evaluates
/// after each step; that is the idea worth taking, and this is the part of it
/// that can be decided rather than hoped for.
///
/// Three rules:
///
///   A round where nothing succeeded is not a step done.
///
///   The first such round asks the model to revise rather than continue. Not the
///   third — by then it has spent two rounds acting on something it already knew
///   was wrong.
///
///   Enough consecutive failures, or enough rewrites, and it stops. A plan
///   rewritten four times is not planning, and burning the whole round budget to
///   arrive at the same place wastes a person's time and their tokens.
/// </summary>
public sealed class PlanProgress
{
    /// <summary>
    /// How many rounds in a row may fail before giving up.
    ///
    /// Two, because the first failure buys a revision and the second says the
    /// revision did not help either. A third round is unlikely to be the one that
    /// works, and every round costs a request.
    /// </summary>
    public const int MaxConsecutiveFailures = 2;

    /// <summary>
    /// How many times a plan may be rewritten before it counts as thrashing.
    ///
    /// Revising once is thinking. Revising three times inside one turn is a model
    /// going round in circles with a fresh list each lap.
    /// </summary>
    public const int MaxRevisions = 3;

    private IReadOnlyList<string> _steps = [];

    /// <summary>The steps as last stated.</summary>
    public IReadOnlyList<string> Steps => _steps;

    /// <summary>How many are done. Never more than there are.</summary>
    public int Done { get; private set; }

    /// <summary>How many times the plan has been rewritten this turn.</summary>
    public int Revisions { get; private set; }

    /// <summary>Rounds that have failed back to back.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Whether anything is being tracked at all.</summary>
    public bool HasPlan => _steps.Count > 0;

    /// <summary>
    /// Records a plan the model stated.
    ///
    /// A plan identical to the one already showing is not a revision — models
    /// restate things, and counting that as a rewrite would stop a turn that is
    /// going perfectly well.
    /// </summary>
    /// <returns>True if this replaced a different plan.</returns>
    public bool State(IReadOnlyList<string> steps)
    {
        if (steps is null || steps.Count == 0)
        {
            return false;
        }

        if (_steps.Count > 0 && _steps.SequenceEqual(steps, StringComparer.Ordinal))
        {
            return false;
        }

        var revised = _steps.Count > 0;

        _steps = steps;
        Done = 0;

        if (revised)
        {
            Revisions++;
        }

        return revised;
    }

    /// <summary>
    /// Judges one round of tool calls.
    /// </summary>
    /// <param name="succeeded">One entry per call made this round.</param>
    public RoundVerdict Round(IReadOnlyList<bool> succeeded)
    {
        var calls = succeeded ?? [];

        // A round with no calls is the model talking rather than acting. It is
        // not progress and it is not a failure — there is nothing to judge.
        if (calls.Count == 0)
        {
            return new RoundVerdict(false, false, false, null);
        }

        if (calls.Any(ok => ok))
        {
            ConsecutiveFailures = 0;

            var completed = HasPlan && Done < _steps.Count;
            if (completed)
            {
                Done++;
            }

            return new RoundVerdict(completed, false, false, null);
        }

        ConsecutiveFailures++;

        if (Revisions >= MaxRevisions)
        {
            return new RoundVerdict(false, false, true,
                $"The plan has been rewritten {Revisions} times and the last round still failed. Stopping.");
        }

        if (ConsecutiveFailures >= MaxConsecutiveFailures)
        {
            return new RoundVerdict(false, false, true,
                $"{ConsecutiveFailures} rounds in a row failed. Stopping rather than trying the same thing again.");
        }

        return new RoundVerdict(false, true, false, RevisionRequest());
    }

    /// <summary>Forgets everything. A new turn starts with no plan.</summary>
    public void Clear()
    {
        _steps = [];
        Done = 0;
        Revisions = 0;
        ConsecutiveFailures = 0;
    }

    /// <summary>
    /// What the model is told after a round where nothing worked.
    ///
    /// Addressed to the plan rather than to the failure: the model can already see
    /// the error text in the tool results. What it cannot see is that the rest of
    /// its plan is now standing on something that did not happen.
    /// </summary>
    private string RevisionRequest()
        => HasPlan
            ? "That step did not work. The rest of the plan assumed it did — state a new "
              + "```plan block with what you now intend, or say plainly that you cannot do this."
            : "That did not work. Say what you intend to try instead, or say plainly that you cannot do this.";
}
