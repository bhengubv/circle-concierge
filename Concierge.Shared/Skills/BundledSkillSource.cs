using System.Reflection;

namespace Concierge.Shared.Skills;

/// <summary>Lets the runtime activate the same curated skills already visible in the catalog.</summary>
public sealed class BundledSkillSource : ISkillSource
{
    private readonly Assembly _assembly = typeof(SkillCatalogService).Assembly;
    private IReadOnlyDictionary<string, string>? _resources;

    public string Name => "bundled";

    public IReadOnlyList<SkillDescriptor> Discover()
    {
        _resources ??= _assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Skills.Curated.", StringComparison.Ordinal)
                && name.EndsWith(".SKILL.md", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                name => name.Split(".Skills.Curated.", 2, StringSplitOptions.None)[1][..^".SKILL.md".Length].Replace('_', '-'),
                name => name,
                StringComparer.OrdinalIgnoreCase);

        return new SkillCatalogService().GetSkills()
            .Where(skill => _resources.ContainsKey(skill.Id))
            .Select(skill => new SkillDescriptor(skill.Id, skill.Name, skill.Area, skill.Summary,
                $"embed://{skill.Id}", Name, [], null, []))
            .ToList();
    }

    public string? LoadBody(SkillDescriptor descriptor)
    {
        if (_resources is null) Discover();
        if (_resources is null || !_resources.TryGetValue(descriptor.Id, out var resourceName)) return null;
        using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        if (!content.StartsWith("---", StringComparison.Ordinal)) return content;
        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? content : content[(end + 4)..].TrimStart('\r', '\n');
    }
}
