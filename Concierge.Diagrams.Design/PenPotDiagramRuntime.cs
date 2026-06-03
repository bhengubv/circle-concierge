using System.Text.Json;
using Concierge.Shared.Diagrams;

namespace Concierge.Diagrams.Design;

/// <summary>
/// <see cref="IDiagramRuntime"/> that translates a PenPot file JSON tree into the host-neutral
/// <see cref="DiagramArtifact"/> shape. Source is preserved verbatim so the chat UI can show
/// the user exactly what was imported; the structured element list flattens every shape in
/// every page so the agent can answer questions like "which frames mention pricing?" or
/// "how deep is the component tree?" without re-parsing JSON.
/// </summary>
public sealed class PenPotDiagramRuntime : IDiagramRuntime
{
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly PenPotApiOptions _options;

    /// <summary>
    /// Parameterless constructor for hosts that only need the file-source codepath — token
    /// status reports as "not configured" until <see cref="AddConciergePenPotDiagrams"/> wires
    /// it with real options.
    /// </summary>
    public PenPotDiagramRuntime() : this(new PenPotApiOptions())
    {
    }

    public PenPotDiagramRuntime(PenPotApiOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => "penpot";

    public string DisplayName => "PenPot";

    public IReadOnlyList<string> SupportedExtensions { get; } = ["penpot.json", "json"];

    public bool IsReady => !string.IsNullOrWhiteSpace(_options.AccessToken);

    public string StatusMessage => IsReady
        ? $"Ready · {_options.BaseAddress}"
        : "PenPot access token not configured — set PenPot:AccessToken in IConfiguration to enable.";

    public async Task<DiagramArtifact> ImportAsync(string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var reader = new StreamReader(content, leaveOpen: true);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var file = PenPotApiClient.ParseFileJson(json);
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
            var file = PenPotApiClient.ParseFileJson(source);
            return Translate(file, source);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Produces an artifact from a strongly-typed file already fetched via the REST client.
    /// Avoids the round-trip through string JSON when the caller has the object in hand.
    /// </summary>
    public DiagramArtifact Translate(PenPotFile file)
        => Translate(file, JsonSerializer.Serialize(file, IndentedJson));

    private DiagramArtifact Translate(PenPotFile file, string source)
    {
        var pages = file.Data?.PagesIndex is { Count: > 0 }
            ? file.Data.PagesIndex.Values.ToList()
            : new List<PenPotPage>();

        var elements = new List<DiagramElement>();
        foreach (var page in pages)
        {
            // Each page itself becomes an element so the agent can speak about pages by name.
            elements.Add(new DiagramElement(
                Id: page.Id.ToString(),
                ParentId: null,
                ElementType: "page",
                Name: page.Name ?? "Untitled page",
                Metadata: new Dictionary<string, string>
                {
                    ["objectCount"] = (page.Objects?.Count ?? 0).ToString(),
                }));

            if (page.Objects is null)
            {
                continue;
            }

            foreach (var (id, shape) in page.Objects)
            {
                // Skip the synthetic root shape — PenPot stores it under a sentinel id.
                if (string.Equals(id, "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal))
                {
                    continue;
                }

                elements.Add(new DiagramElement(
                    Id: shape.Id ?? id,
                    ParentId: shape.ParentId,
                    ElementType: shape.Type ?? "shape",
                    Name: shape.Name ?? shape.Type ?? "Unnamed",
                    Metadata: new Dictionary<string, string>
                    {
                        ["pageId"] = page.Id.ToString(),
                        ["childCount"] = (shape.Children?.Count ?? 0).ToString(),
                    }));
            }
        }

        var frameCount = elements.Count(e => string.Equals(e.ElementType, "frame", StringComparison.OrdinalIgnoreCase));
        var componentCount = elements.Count(e => string.Equals(e.ElementType, "component", StringComparison.OrdinalIgnoreCase));
        var summary =
            $"PenPot file '{file.Name ?? "Untitled"}' — {pages.Count} page(s), " +
            $"{elements.Count} element(s) total ({frameCount} frame(s), {componentCount} component(s)).";

        return new DiagramArtifact(
            RuntimeId: Id,
            Kind: "ui-mockup",
            Title: file.Name ?? "PenPot file",
            Source: source,
            Summary: summary,
            Elements: elements);
    }
}
