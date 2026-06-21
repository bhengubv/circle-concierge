using System.Reflection;
using System.Text.RegularExpressions;
using Concierge.Shared.Skills;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Concierge.Shared;

public interface ISkillCatalogService
{
    IReadOnlyList<SkillInfo> GetSkills();
}

/// <summary>
/// Aggregated catalog backed by any number of <see cref="ISkillSource"/>s plus
/// the legacy embedded-manifest-resource bundle.
///
/// The MAUI host ships the embedded bundle (so the app always has SOME skills
/// even without internet/filesystem access). The desktop / dev host adds a
/// <see cref="FileSystemSkillSource"/> pointing at
/// <c>C:\Dev\Solutions\com.bhengubv\Skills\</c> so the full library lights up
/// at dev time — including the 51 skills from <c>Claude-BugHunter</c>, the
/// loki-mode SKILL.md, anthropic-skills, vercel-agent-skills, etc.
///
/// All sources are queried at construction time; results are merged + deduped
/// by id (first source wins) and cached for the process lifetime.
/// </summary>
public sealed class SkillCatalogService : ISkillCatalogService
{
    private readonly Lazy<IReadOnlyList<SkillInfo>> _skills;

    /// <summary>Default constructor — embedded bundle only. Used when no
    /// <see cref="ISkillSource"/>s are registered in DI (e.g. minimal MAUI
    /// runtime without filesystem access).</summary>
    public SkillCatalogService() : this(Enumerable.Empty<ISkillSource>()) { }

    public SkillCatalogService(IEnumerable<ISkillSource> sources)
    {
        var src = sources?.ToList() ?? new List<ISkillSource>();
        _skills = new Lazy<IReadOnlyList<SkillInfo>>(() => Compose(src));
    }

    public IReadOnlyList<SkillInfo> GetSkills() => _skills.Value;

    private static IReadOnlyList<SkillInfo> Compose(IReadOnlyList<ISkillSource> sources)
    {
        // Embedded bundle first — these are the curated bundled skills that
        // SHIP inside the assembly. They're already namespaced + deterministic.
        var collected = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in LoadBundledSkills())
        {
            collected[s.Id] = s;
        }

        // Filesystem / downloaded sources next. Later sources can ADD new ids
        // but never override a bundled one (defensive — bundled is the contract).
        foreach (var source in sources)
        {
            foreach (var descriptor in source.Discover())
            {
                if (!collected.ContainsKey(descriptor.Id))
                {
                    collected[descriptor.Id] = descriptor.ToInfo();
                }
            }
        }

        return collected.Values
            .OrderBy(s => s.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Embedded bundle (legacy path) ──────────────────────────────────────

    private static IReadOnlyList<SkillInfo> LoadBundledSkills()
    {
        var assembly = typeof(SkillCatalogService).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Skills.Curated.", StringComparison.Ordinal)
                && name.EndsWith(".SKILL.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skills = new List<SkillInfo>();
        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            skills.Add(ParseSkill(resourceName, content));
        }
        return skills
            .OrderBy(skill => skill.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static SkillInfo ParseSkill(string resourceName, string content)
    {
        var metadata = ParseFrontMatter(content);
        var id = metadata.GetValueOrDefault("name") ?? ExtractIdFromResourceName(resourceName);
        var name = metadata.GetValueOrDefault("profile")
            ?? Humanize(metadata.GetValueOrDefault("name") ?? id);
        var category = metadata.GetValueOrDefault("category") ?? "General";
        var description = metadata.GetValueOrDefault("description")
            ?? ExtractFirstParagraph(content)
            ?? "Curated Concierge skill.";
        return new SkillInfo(id, name, category, description);
    }

    private static Dictionary<string, string> ParseFrontMatter(string content)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---", StringComparison.Ordinal)) return metadata;
        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return metadata;
        var frontMatter = content[3..end];
        try
        {
            using var reader = new StringReader(frontMatter);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode mapping) return metadata;
            foreach (var (rawKey, rawValue) in mapping.Children)
            {
                if (rawKey is YamlScalarNode keyNode && rawValue is YamlScalarNode valueNode
                    && keyNode.Value is { } key && valueNode.Value is { } value)
                {
                    metadata[key] = value;
                }
            }
        }
        catch (YamlException) { /* malformed — fall through */ }
        return metadata;
    }

    private static string? ExtractFirstParagraph(string content)
    {
        return content
            .ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(block => !block.StartsWith("---", StringComparison.Ordinal) && !block.StartsWith('#'));
    }

    private static string ExtractIdFromResourceName(string resourceName)
    {
        var match = Regex.Match(resourceName, @"\.Curated\.([^.]+)\.SKILL\.md$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace('_', '-') : Path.GetFileNameWithoutExtension(resourceName).ToLowerInvariant();
    }

    private static string Humanize(string value)
    {
        return string.Join(' ', value.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
