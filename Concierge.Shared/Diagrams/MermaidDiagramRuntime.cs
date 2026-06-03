using System.Text.RegularExpressions;

namespace Concierge.Shared.Diagrams;

/// <summary>
/// Runtime for Mermaid text-DSL diagrams. Parses just enough of the source header to classify
/// the diagram kind and pull a title (if the model emitted a <c>title</c> directive) — the
/// actual SVG rendering happens client-side via <c>mermaid.js</c>, kicked off by the
/// <c>MermaidBlock</c> Razor component.
/// </summary>
public sealed class MermaidDiagramRuntime : IDiagramRuntime
{
    /// <summary>Regex applied to the first non-empty source line to classify a diagram.</summary>
    private static readonly Regex KindHeader = new(
        @"^\s*(graph|flowchart|sequenceDiagram|classDiagram|stateDiagram(-v2)?|erDiagram|journey|gantt|gitGraph|mindmap|timeline|pie|quadrantChart|requirementDiagram|C4Context|C4Container|C4Component|C4Dynamic|C4Deployment|sankey-beta|xychart-beta|block-beta|architecture-beta|packet-beta)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(500));

    private static readonly Regex TitleDirective = new(
        @"^\s*title\s*:?\s*(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline,
        TimeSpan.FromMilliseconds(500));

    /// <summary>
    /// Matches fenced Mermaid blocks in a Markdown-ish reply. The non-backtracking engine
    /// keeps this safe against pathological inputs lifted from streamed LLM output.
    /// </summary>
    private static readonly Regex FencedBlock = new(
        @"```mermaid\s*\n(?<body>[\s\S]*?)\n```",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    public string Id => "mermaid";

    public string DisplayName => "Mermaid";

    public IReadOnlyList<string> SupportedExtensions { get; } = ["mmd", "mermaid"];

    public bool IsReady => true;

    public string StatusMessage => "Text-DSL — paste source to render.";

    public Task<DiagramArtifact> ImportAsync(string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, leaveOpen: true);
        var source = reader.ReadToEnd();
        var artifact = FromSource(source)
            ?? throw new InvalidOperationException("File does not contain a recognisable Mermaid diagram.");
        return Task.FromResult(artifact with { Title = string.IsNullOrWhiteSpace(artifact.Title) ? fileName : artifact.Title });
    }

    public DiagramArtifact? FromSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var trimmed = source.Trim();
        var firstLine = trimmed.Split('\n', 2)[0];
        var match = KindHeader.Match(firstLine);
        if (!match.Success)
        {
            return null;
        }

        var kind = NormaliseKind(match.Groups[1].Value);
        var title = ExtractTitle(trimmed) ?? $"{char.ToUpperInvariant(kind[0])}{kind[1..]} diagram";
        var summary = $"Mermaid {kind} block with {CountNodes(trimmed)} declared lines.";

        return new DiagramArtifact(
            RuntimeId: Id,
            Kind: kind,
            Title: title,
            Source: trimmed,
            Summary: summary,
            Elements: Array.Empty<DiagramElement>());
    }

    /// <summary>
    /// Extracts every fenced <c>```mermaid</c> block in a streamed assistant reply. Used by
    /// the chat UI to walk replies and substitute live-rendered diagrams for the raw source.
    /// </summary>
    public static IReadOnlyList<MermaidBlockMatch> ExtractFencedBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<MermaidBlockMatch>();
        }

        try
        {
            var matches = FencedBlock.Matches(text);
            var blocks = new List<MermaidBlockMatch>(matches.Count);
            foreach (Match m in matches)
            {
                blocks.Add(new MermaidBlockMatch(
                    Start: m.Index,
                    Length: m.Length,
                    Source: m.Groups["body"].Value));
            }
            return blocks;
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<MermaidBlockMatch>();
        }
    }

    private static string NormaliseKind(string raw)
    {
        var lower = raw.ToLowerInvariant();
        return lower switch
        {
            "graph" or "flowchart" => "flowchart",
            "sequencediagram" => "sequence",
            "classdiagram" => "class",
            "statediagram" or "statediagram-v2" => "state",
            "erdiagram" => "er",
            "journey" => "user-journey",
            "gantt" => "gantt",
            "gitgraph" => "git",
            "mindmap" => "mindmap",
            "timeline" => "timeline",
            "pie" => "pie",
            "quadrantchart" => "quadrant",
            "requirementdiagram" => "requirements",
            var c4 when c4.StartsWith("c4") => "c4",
            "sankey-beta" => "sankey",
            "xychart-beta" => "xy-chart",
            "block-beta" => "block",
            "architecture-beta" => "architecture",
            "packet-beta" => "packet",
            _ => "unknown",
        };
    }

    private static string? ExtractTitle(string source)
    {
        var match = TitleDirective.Match(source);
        return match.Success ? match.Groups["title"].Value : null;
    }

    private static int CountNodes(string source)
    {
        var count = 0;
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("%%", StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }
}

/// <summary>Position + body of a Mermaid fenced block lifted from text.</summary>
public sealed record MermaidBlockMatch(int Start, int Length, string Source);
