using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Concierge.Shared.Skills;

/// <summary>
/// Walks one or more directory roots and surfaces every <c>SKILL.md</c> file as
/// a discoverable skill. Front-matter is parsed for metadata; the markdown body
/// is loaded lazily by <see cref="LoadBody"/> when a skill is activated.
///
/// This is how Concierge picks up the local skill library at
/// <c>C:\Dev\Solutions\com.bhengubv\Skills\</c> on dev machines plus any
/// repos cloned into <c>02 - Downloaded Skill Sources\</c> (Claude-BugHunter,
/// loki-mode, anthropic-skills, vercel-agent-skills, etc.). Without this source
/// the catalog would only see the ~30 skills compiled into the
/// <c>Concierge.Shared</c> assembly.
/// </summary>
public sealed class FileSystemSkillSource : ISkillSource
{
    private readonly string[] _roots;
    private readonly string _name;
    public string Name => _name;

    public FileSystemSkillSource(string name, params string[] roots)
    {
        _name = name;
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
    }

    public IReadOnlyList<SkillDescriptor> Discover()
    {
        var found = new List<SkillDescriptor>();
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            // SKILL.md is the standard Anthropic skill file name. SKILL.md
            // located at any depth — a single repo can ship hundreds.
            foreach (var path in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                var d = TryReadDescriptor(path, root);
                if (d is not null) found.Add(d);
            }
        }
        return found;
    }

    public string? LoadBody(SkillDescriptor descriptor)
    {
        if (!descriptor.BodyUri.StartsWith("file://", StringComparison.Ordinal)) return null;
        var path = descriptor.BodyUri.Substring("file://".Length);
        if (!File.Exists(path)) return null;
        var content = File.ReadAllText(path);
        return StripFrontMatter(content);
    }

    private SkillDescriptor? TryReadDescriptor(string path, string root)
    {
        string content;
        try { content = File.ReadAllText(path); }
        catch { return null; }

        var metadata = ParseFrontMatter(content);
        // Skill id derives from the containing folder name when not declared.
        var folder = Path.GetFileName(Path.GetDirectoryName(path)!);
        var id = metadata.GetValueOrDefault("name") ?? folder;
        var name = metadata.GetValueOrDefault("profile") ?? metadata.GetValueOrDefault("title") ?? Humanize(id);
        var area = metadata.GetValueOrDefault("category") ?? metadata.GetValueOrDefault("area") ?? GuessAreaFromPath(path, root);
        var description = metadata.GetValueOrDefault("description") ?? ExtractFirstParagraph(content) ?? "Skill imported from disk.";

        // Optional metadata fields.
        var tools = SplitCsv(metadata.GetValueOrDefault("tools"));
        var preferredProvider = metadata.GetValueOrDefault("provider")
                                ?? metadata.GetValueOrDefault("model")
                                ?? metadata.GetValueOrDefault("recommended_model");
        var tags = SplitCsv(metadata.GetValueOrDefault("tags"));

        // Source tag derives from the root folder so the UI can badge "from
        // Claude-BugHunter" vs "from anthropic-skills".
        var sourceTag = $"{_name}:{ShortRoot(root)}";

        return new SkillDescriptor(
            Id: $"{ShortRoot(root)}/{id}",   // namespace so same-name skills from different sources don't collide
            Name: name,
            Area: area,
            Description: description.Length > 280 ? description[..280] + "…" : description,
            BodyUri: "file://" + path.Replace('\\', '/'),
            Source: sourceTag,
            Tools: tools,
            PreferredProvider: preferredProvider,
            Tags: tags);
    }

    // ── Front-matter parsing ───────────────────────────────────────────────

    /// <summary>Parse the YAML front-matter block. Resilient to malformed YAML
    /// — returns whatever could be parsed.</summary>
    private static Dictionary<string, string> ParseFrontMatter(string content)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---", StringComparison.Ordinal)) return metadata;
        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return metadata;
        var fm = content[3..end];

        try
        {
            using var reader = new StringReader(fm);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode map) return metadata;
            foreach (var (k, v) in map.Children)
            {
                if (k is YamlScalarNode keyNode && v is YamlScalarNode valueNode
                    && keyNode.Value is { } key && valueNode.Value is { } value)
                {
                    metadata[key] = value;
                }
            }
        }
        catch (YamlException) { /* fall through — heuristic recovery below */ }
        return metadata;
    }

    private static string StripFrontMatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return content;
        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return content;
        return content[(end + 4)..].TrimStart('\n');
    }

    private static string? ExtractFirstParagraph(string content)
    {
        var stripped = StripFrontMatter(content).ReplaceLineEndings("\n");
        return stripped.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(b => !b.StartsWith('#') && b.Length > 20);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string GuessAreaFromPath(string path, string root)
    {
        // path = <root>/<area>/<skill>/SKILL.md OR <root>/<skill>/SKILL.md.
        // Pick the immediate parent of <skill> as the area, fallback "General".
        try
        {
            var rel = Path.GetRelativePath(root, Path.GetDirectoryName(path)!);
            var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return Humanize(parts[^2]);
        }
        catch { }
        return "General";
    }

    private static string ShortRoot(string root)
    {
        return Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static IReadOnlyList<string> SplitCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string Humanize(string value)
    {
        return string.Join(' ', value
            .Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..] : string.Empty)));
    }
}
