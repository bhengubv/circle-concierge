namespace Concierge.Shared.Chat;

/// <summary>
/// Which order to ask them in.
///
/// <see cref="RuntimeFailover"/> answers "may this one be asked" — never off the
/// device, never past a refusal, never after the first token. It says nothing
/// about *order*, so the chain was whatever order the alternatives happened to
/// arrive in, forever. A provider that failed thirty seconds ago was tried first
/// again, and the one that has been answering all afternoon waited behind it.
///
/// Antra resolves fifteen sources rather than listing them: priority tiers,
/// rotation within a tier to spread load, and a rate-limited source demoted to the
/// back rather than dropped. Three of those carry over; one does not, and saying
/// which is part of the decision.
///
///   **Tiers** — the local engine is its own tier and comes first, because it is
///   what the product is. Everything else shares a tier.
///
///   **Demotion, not removal** — a runtime that just failed goes to the back and
///   stays reachable. Dropping it would mean a bad thirty seconds costs a provider
///   for the rest of the session, and a chain that quietly gets shorter is a
///   product that quietly gets less able.
///
///   **Rotation** — among runtimes that are equally healthy, start somewhere
///   different each time rather than hammering whichever happens to be first.
///
///   **Per-source accept thresholds** are declined. Antra scores a candidate
///   against what was asked for and rejects a poor match. A chat reply has no
///   such score, and inventing one would mean deciding an answer was not good
///   enough and silently asking somebody else — which is the shopping-for-a-yes
///   that <see cref="RuntimeFailover"/> exists to forbid.
///
/// Deliberately not thread-safe beyond the lock it holds: one person, one
/// conversation at a time. A lock rather than nothing because the failure is
/// recorded from whichever thread the stream died on.
/// </summary>
public sealed class RuntimeResolver
{
    /// <summary>
    /// How long a runtime stays at the back after it fails.
    ///
    /// Long enough to get past the bad minute that made it fail; short enough
    /// that a provider is not punished for the rest of an afternoon. It is
    /// deliberately not exponential — a person who sends three messages while
    /// something is briefly down should not find their good provider demoted for
    /// an hour because it blipped once.
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, DateTimeOffset> _failedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private int _turn;

    public RuntimeResolver(Func<DateTimeOffset>? now = null)
        => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Remembers that one failed, so the next turn does not lead with it.
    ///
    /// Called on a failure rather than on a refusal. A model that declines to
    /// answer has answered, and holding that against the provider would demote it
    /// for doing its job.
    /// </summary>
    public void RecordFailure(IChatRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        lock (_gate)
        {
            _failedAt[runtime.Id] = _now();
        }
    }

    /// <summary>Forgets a failure, because it answered.</summary>
    public void RecordSuccess(IChatRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        lock (_gate)
        {
            _failedAt.Remove(runtime.Id);
        }
    }

    /// <summary>Whether this one is currently at the back.</summary>
    public bool IsCoolingDown(IChatRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        lock (_gate)
        {
            return CoolingDown(runtime, _now());
        }
    }

    /// <summary>
    /// The alternatives, in the order they should be asked.
    ///
    /// Healthy ones first, rotated; recently-failed ones after them, oldest
    /// failure first — if everything is in cooldown the least-recently-broken is
    /// the best remaining guess. Nothing is ever removed here: what may be asked
    /// is <see cref="RuntimeFailover"/>'s decision, and two places quietly
    /// shortening the chain is how a provider disappears without anybody choosing
    /// that it should.
    /// </summary>
    public IReadOnlyList<IChatRuntime> Order(IReadOnlyList<IChatRuntime>? alternatives)
    {
        if (alternatives is null || alternatives.Count == 0)
        {
            return [];
        }

        lock (_gate)
        {
            var now = _now();
            var healthy = new List<IChatRuntime>();
            var cooling = new List<IChatRuntime>();

            foreach (var runtime in alternatives)
            {
                if (runtime is null)
                {
                    continue;
                }

                (CoolingDown(runtime, now) ? cooling : healthy).Add(runtime);
            }

            // Oldest failure first: if every one is cooling, the one that broke
            // longest ago is the likeliest to have recovered.
            cooling.Sort((left, right) => _failedAt[left.Id].CompareTo(_failedAt[right.Id]));

            var rotated = Rotate(healthy);
            rotated.AddRange(cooling);
            return rotated;
        }
    }

    /// <summary>
    /// Starts the healthy list at a different place each turn.
    ///
    /// Round-robin rather than random so a test can say what happens, and so two
    /// people reporting different behaviour are describing two different states
    /// rather than luck. The counter only advances when there is something to
    /// rotate, which keeps "the second turn starts with the second provider" true
    /// rather than dependent on how many turns had no alternatives at all.
    /// </summary>
    private List<IChatRuntime> Rotate(List<IChatRuntime> healthy)
    {
        if (healthy.Count <= 1)
        {
            return healthy;
        }

        var start = _turn % healthy.Count;
        _turn = unchecked(_turn + 1);

        var ordered = new List<IChatRuntime>(healthy.Count);
        for (var i = 0; i < healthy.Count; i++)
        {
            ordered.Add(healthy[(start + i) % healthy.Count]);
        }

        return ordered;
    }

    private bool CoolingDown(IChatRuntime runtime, DateTimeOffset now)
        => _failedAt.TryGetValue(runtime.Id, out var when) && now - when < Cooldown;
}
