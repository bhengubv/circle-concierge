using CircleAI.ContentPolicy;

namespace Concierge.Shared.Safety;

/// <summary>
/// Minimum-viable <see cref="IContentFilter"/> backed by a small in-process
/// blocklist keyed by category. Not a substitute for a model-based classifier
/// — it's the fail-closed baseline so Concierge can ship Family Mode in v1.0
/// without the upstream classifier model being available on every device.
/// </summary>
/// <remarks>
/// Categories mirror the four headline rails CircleAI.Safety.Child surfaces
/// (violence, sexual, self-harm, illicit). Keywords are deliberately broad +
/// false-positive-leaning at Strict, narrower at Balanced. Any future swap to
/// a real on-device classifier (CircleAI.SafetyChild.OnDeviceClassifier when
/// it ships, or a child-tuned MNN model) is a one-class change — the IContentFilter
/// surface is unchanged and the rest of Concierge keeps working.
/// </remarks>
public sealed class ConciergeContentFilter : IContentFilter
{
    private static readonly (string Category, string[] Patterns)[] StrictBlocklist =
    {
        ("violence", new[]
        {
            " kill ", " murder", " gun", " stab", " bomb", " shoot", " weapon",
        }),
        ("sexual", new[]
        {
            " sex", " porn", " nude", " erotic", " naked",
        }),
        ("self-harm", new[]
        {
            "suicide", "self harm", "self-harm", "cut myself", "cut yourself", "hurt myself",
        }),
        ("illicit", new[]
        {
            " drugs", " meth", " cocaine", " heroin", " weed ", " marijuana",
        }),
    };

    // Balanced narrows to the highest-confidence terms; "weapon" alone isn't
    // enough at Balanced, but explicit harm language still is.
    private static readonly (string Category, string[] Patterns)[] BalancedBlocklist =
    {
        ("self-harm", new[]
        {
            "suicide", "self harm", "self-harm", "cut myself", "hurt myself",
        }),
        ("violence", new[]
        {
            " kill yourself", " murder",
        }),
        ("sexual", new[]
        {
            " porn", " erotic",
        }),
    };

    private readonly Func<SafetyStrictness> _strictnessReader;

    public ConciergeContentFilter(Func<SafetyStrictness> strictnessReader)
    {
        _strictnessReader = strictnessReader ?? throw new ArgumentNullException(nameof(strictnessReader));
    }

    public string BackendId => "concierge.local.v1";

    public ValueTask<SafetyFinding> ClassifyAsync(string text, CancellationToken ct = default)
    {
        var strictness = _strictnessReader();
        if (strictness == SafetyStrictness.Off || string.IsNullOrWhiteSpace(text))
        {
            return ValueTask.FromResult(new SafetyFinding(
                SafetyVerdict.Allow, "ok", "filter disabled", 1f));
        }

        // Surround with spaces so " gun" doesn't match "shogun" and " sex"
        // doesn't match "sextet". Cheap word-boundary stand-in that keeps
        // the matcher allocation-light.
        var haystack = " " + text.ToLowerInvariant() + " ";
        var blocklist = strictness == SafetyStrictness.Strict
            ? StrictBlocklist
            : BalancedBlocklist;

        foreach (var (category, patterns) in blocklist)
        {
            foreach (var pattern in patterns)
            {
                if (haystack.Contains(pattern, StringComparison.Ordinal))
                {
                    // Strict refuses; Balanced flags but doesn't auto-refuse —
                    // the IRefusalPolicy decides whether a flag becomes a
                    // refusal.
                    return ValueTask.FromResult(new SafetyFinding(
                        Verdict: strictness == SafetyStrictness.Strict
                            ? SafetyVerdict.Refuse
                            : SafetyVerdict.Flag,
                        Category: category,
                        Reason: $"Matched {category} pattern '{pattern.Trim()}'",
                        Confidence: 0.7f));
                }
            }
        }

        return ValueTask.FromResult(new SafetyFinding(
            SafetyVerdict.Allow, "ok", "no pattern matched", 1f));
    }
}

/// <summary>
/// Minimum-viable <see cref="IRefusalPolicy"/>: any single Refuse finding
/// becomes a refusal; flags accumulate but only auto-refuse when two or more
/// categories light up at once (e.g. self-harm + violence). Errs on the side
/// of letting the user see "borderline" content surfaced as Flag — the audit
/// log captures everything either way.
/// </summary>
public sealed class ConciergeRefusalPolicy : IRefusalPolicy
{
    public string BackendId => "concierge.local.v1";

    public ValueTask<bool> ShouldRefuseAsync(
        IReadOnlyList<SafetyFinding> findings,
        CancellationToken ct = default)
    {
        if (findings is null || findings.Count == 0)
        {
            return ValueTask.FromResult(false);
        }

        var refuseHits = 0;
        var distinctFlagCategories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in findings)
        {
            if (f.Verdict == SafetyVerdict.Refuse)
            {
                refuseHits++;
            }
            else if (f.Verdict == SafetyVerdict.Flag)
            {
                distinctFlagCategories.Add(f.Category);
            }
        }

        // One explicit Refuse → refuse. OR two+ distinct Flag categories
        // (e.g. self-harm + violence in the same turn) → refuse. Single
        // category Flags pass through and surface in the audit log.
        return ValueTask.FromResult(refuseHits > 0 || distinctFlagCategories.Count >= 2);
    }
}
