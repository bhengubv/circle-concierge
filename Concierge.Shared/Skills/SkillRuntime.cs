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
    /// (e.g. shared style guidance) and orders by descriptor.Name.</summary>
    string ComposeSystemPrompt(IEnumerable<string> skillIds);
}

public sealed class SkillRuntime : ISkillRuntime
{
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

    public string ComposeSystemPrompt(IEnumerable<string> skillIds)
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

        // Multi-skill stack: surround each with a header so the model can
        // identify which instructions apply when.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You have been equipped with the following specialist skill profiles.");
        sb.AppendLine("Apply them in combination — each profile's guidance is concurrently in force.");
        sb.AppendLine();
        foreach (var a in activations)
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
