using System.Text.Json;
using Concierge.Shared.Diagrams;

namespace Concierge.Diagrams.Design;

/// <summary>
/// <see cref="IDiagramRuntime"/> that translates a Figma file JSON tree into the
/// host-neutral artifact shape. Walks the recursive node tree once, producing one
/// <see cref="DiagramElement"/> per node with parent-id links preserved so the agent
/// can reason about composition.
/// </summary>
public sealed class FigmaDiagramRuntime : IDiagramRuntime
{
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string Id => "figma";

    public string DisplayName => "Figma";

    public IReadOnlyList<string> SupportedExtensions { get; } = ["figma.json", "json"];

    public async Task<DiagramArtifact> ImportAsync(string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var reader = new StreamReader(content, leaveOpen: true);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var file = FigmaApiClient.ParseFileJson(json);
        return Translate(file, json);
    }

    public DiagramArtifact? FromSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            var file = FigmaApiClient.ParseFileJson(source);
            if (file.Document is null)
            {
                return null;
            }
            return Translate(file, source);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Produces an artifact from a strongly-typed file already fetched via the REST client.
    /// </summary>
    public DiagramArtifact Translate(FigmaFile file)
        => Translate(file, JsonSerializer.Serialize(file, IndentedJson));

    private DiagramArtifact Translate(FigmaFile file, string source)
    {
        var elements = new List<DiagramElement>();
        if (file.Document is not null)
        {
            Walk(file.Document, parentId: null, elements);
        }

        var pageCount = elements.Count(e => string.Equals(e.ElementType, "CANVAS", StringComparison.OrdinalIgnoreCase));
        var frameCount = elements.Count(e => string.Equals(e.ElementType, "FRAME", StringComparison.OrdinalIgnoreCase));
        var componentCount = elements.Count(e =>
            string.Equals(e.ElementType, "COMPONENT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.ElementType, "COMPONENT_SET", StringComparison.OrdinalIgnoreCase));

        var summary =
            $"Figma file '{file.Name ?? "Untitled"}' — {pageCount} canvas(es), " +
            $"{elements.Count} node(s) total ({frameCount} frame(s), {componentCount} component(s)).";

        return new DiagramArtifact(
            RuntimeId: Id,
            Kind: "ui-mockup",
            Title: file.Name ?? "Figma file",
            Source: source,
            Summary: summary,
            Elements: elements);
    }

    private static void Walk(FigmaNode node, string? parentId, List<DiagramElement> sink)
    {
        sink.Add(new DiagramElement(
            Id: node.Id,
            ParentId: parentId,
            ElementType: node.Type ?? "NODE",
            Name: node.Name ?? node.Type ?? "Unnamed",
            Metadata: new Dictionary<string, string>
            {
                ["childCount"] = (node.Children?.Count ?? 0).ToString(),
            }));

        if (node.Children is null)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            Walk(child, node.Id, sink);
        }
    }
}
