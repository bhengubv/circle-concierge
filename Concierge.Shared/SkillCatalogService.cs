using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Concierge.Shared;

public interface ISkillCatalogService
{
    IReadOnlyList<SkillInfo> GetSkills();
}

public sealed class SkillCatalogService : ISkillCatalogService
{
    private readonly Lazy<IReadOnlyList<SkillInfo>> _skills = new(LoadBundledSkills);

    public IReadOnlyList<SkillInfo> GetSkills()
    {
        return _skills.Value;
    }

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
            if (stream is null)
            {
                continue;
            }

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

    /// <summary>
    /// Parses the YAML front-matter block out of a SKILL.md file. Replaces an earlier hand-rolled
    /// "split on first colon" loop that truncated values containing colons (e.g.
    /// <c>description: "Foo: bar"</c>) and silently ignored multi-line scalars.
    /// </summary>
    private static Dictionary<string, string> ParseFrontMatter(string content)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return metadata;
        }

        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return metadata;
        }

        var frontMatter = content[3..end];

        try
        {
            using var reader = new StringReader(frontMatter);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                return metadata;
            }

            foreach (var (rawKey, rawValue) in mapping.Children)
            {
                if (rawKey is YamlScalarNode keyNode && rawValue is YamlScalarNode valueNode
                    && keyNode.Value is { } key && valueNode.Value is { } value)
                {
                    metadata[key] = value;
                }
            }
        }
        catch (YamlException)
        {
            // Malformed front-matter falls back to whatever resource-name + first-paragraph heuristics
            // can recover; better than blowing up catalog loading for one bad file.
        }

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
