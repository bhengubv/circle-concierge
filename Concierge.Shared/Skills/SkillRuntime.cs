using Microsoft.Extensions.Logging;

namespace Concierge.Shared.Skills;

/// <summary>
/// Loads a skill's body on demand and returns a ready-to-prepend system-prompt
/// addendum. The catalog tells you WHAT exists; the runtime tells you HOW to
/// activate one.
/// </summary>
public interface ISkillRuntime
{
    /// <summary>Resolve a skill id from any registered source to its loaded
    /// body + estimated cost. Returns null when the id is unknown.</summary>
    SkillActivation? Activate(string skillId);

    /// <summary>Compose multiple skills into a single system-prompt addendum.
    /// Skills stack — picking "code review" + "security audit" gives both
    /// instruction sets to the assistant. The runtime de-dupes obvious overlap
    /// (e.g. shared style guidance) and orders by descriptor.Name.
    ///
    /// Bodies are included until the budget runs out; everything past it is named
    /// rather than dropped. See <see cref="SkillRuntime.DefaultBudgetTokens"/>.</summary>
    string ComposeSystemPrompt(IEnumerable<string> skillIds, int budgetTokens = SkillRuntime.DefaultBudgetTokens);
}

public sealed class SkillRuntime : ISkillRuntime
{
    /// <summary>
    /// How much of the prompt the switched-on skills may take before the rest are
    /// named rather than quoted.
    ///
    /// Every activation has computed an `EstimatedTokens` since the day it was
    /// written and nothing has ever read it, so there was no budget at all: switch
    /// on twenty skills and twenty full bodies went into every message, for the
    /// whole conversation, whether or not any of them applied.
    ///
    /// Hallmark's rule is "index-then-pick" — load the index, then only the few
    /// entries you actually need, because pre-loading the rest "costs ~7K tokens
    /// for nothing". Its 69-gate quality checklist is loaded at step 7 rather than
    /// step 1 for exactly this reason: the gates inform fixes, not generation.
    ///
    /// 8,000 is about a quarter of a small local model's window and a rounding
    /// error on a large cloud one, which is the right place to put it: the budget
    /// exists to stop the pathological case, not to police ordinary use. Two or
    /// three skills will never reach it.
    /// </summary>
    public const int DefaultBudgetTokens = 8_000;

    private readonly ISkillCatalogService _catalog;
    private readonly IEnumerable<ISkillSource> _sources;
    private readonly ILogger<SkillRuntime>? _log;

    public SkillRuntime(
        ISkillCatalogService catalog,
        IEnumerable<ISkillSource> sources,
        ILogger<SkillRuntime>? log = null)
    {
        _catalog = catalog;
        _sources = sources;
        _log = log;
    }

    public SkillActivation? Activate(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return null;
        // The catalog stores SkillDescriptors keyed by id when it composes
        // FileSystemSkillSource etc; for backwards compat we also walk every
        // source ourselves and pick the first match.
        SkillDescriptor? descriptor = null;
        ISkillSource? owningSource = null;
        foreach (var src in _sources)
        {
            foreach (var d in src.Discover())
            {
                if (string.Equals(d.Id, skillId, StringComparison.OrdinalIgnoreCase))
                {
                    descriptor = d;
                    owningSource = src;
                    break;
                }
            }
            if (descriptor is not null) break;
        }
        if (descriptor is null || owningSource is null)
        {
            _log?.LogDebug("skill not found: {Id}", skillId);
            return null;
        }

        var body = owningSource.LoadBody(descriptor);
        if (string.IsNullOrWhiteSpace(body))
        {
            _log?.LogDebug("skill body empty / unreachable: {Id} via {Source}", skillId, owningSource.Name);
            return null;
        }

        var addendum = BuildAddendum(descriptor, body);
        var tokens = body.Length / 4;   // rough — 4 chars/token is fine for budgeting

        return new SkillActivation(descriptor, body, addendum, tokens);
    }

    public string ComposeSystemPrompt(IEnumerable<string> skillIds, int budgetTokens = DefaultBudgetTokens)
    {
        var activations = skillIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Activate)
            .Where(a => a is not null)
            .Cast<SkillActivation>()
            .OrderBy(a => a.Descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (activations.Count == 0) return string.Empty;
        if (activations.Count == 1) return activations[0].SystemPromptAddendum;

        // Spend the budget in the order they are shown, so which skills get
        // quoted in full is something a person can predict from the list in front
        // of them rather than an internal ranking they cannot see.
        var quoted = new List<SkillActivation>();
        var named = new List<SkillActivation>();
        var spent = 0;

        foreach (var activation in activations)
        {
            // The first is always quoted whatever it costs. A budget that can
            // reject everything produces a prompt that mentions skills and
            // contains none of them, which is worse than being over.
            if (quoted.Count == 0 || spent + activation.EstimatedTokens <= budgetTokens)
            {
                quoted.Add(activation);
                spent += activation.EstimatedTokens;
            }
            else
            {
                named.Add(activation);
            }
        }

        // Multi-skill stack: surround each with a header so the model can
        // identify which instructions apply when.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You have been equipped with the following specialist skill profiles.");
        sb.AppendLine("Apply them in combination — each profile's guidance is concurrently in force.");
        sb.AppendLine();
        foreach (var a in quoted)
        {
            sb.AppendLine($"## Skill: {a.Descriptor.Name}");
            if (!string.IsNullOrWhiteSpace(a.Descriptor.Description))
            {
                sb.AppendLine($"_{a.Descriptor.Description}_");
            }
            sb.AppendLine();
            sb.AppendLine(a.Body);
            sb.AppendLine();
        }

        // Named rather than dropped. A skill that vanished silently would leave
        // somebody who switched it on watching it do nothing, with no way to tell
        // that from a skill that simply did not help — and the honest answer costs
        // one line each.
        if (named.Count > 0)
        {
            sb.AppendLine("## Also switched on, not quoted here");
            sb.AppendLine(
                "These are active but their full instructions were left out to keep the prompt within "
                + "budget. Ask for one by name if you need its detail.");
            sb.AppendLine();

            foreach (var a in named)
            {
                sb.AppendLine(string.IsNullOrWhiteSpace(a.Descriptor.Description)
                    ? $"- {a.Descriptor.Name}"
                    : $"- {a.Descriptor.Name} — {a.Descriptor.Description}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildAddendum(SkillDescriptor descriptor, string body)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Active skill: {descriptor.Name}");
        if (!string.IsNullOrWhiteSpace(descriptor.Description))
        {
            sb.AppendLine($"_{descriptor.Description}_");
        }
        sb.AppendLine();
        sb.AppendLine(body);
        return sb.ToString();
    }
}
